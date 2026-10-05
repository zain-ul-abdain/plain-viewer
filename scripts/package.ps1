# Builds the offline installer artifacts\installer\PlainViewer-Setup-<version>-<arch>.exe (see docs/RELEASING.md);
# -Arch arm64 builds the native ARM64 installer (default x64).
# 1. Runs the test scripts (skip with -SkipTests).
# 2. Publishes the app and its worker with .NET included (no .NET install needed on the user's PC).
# 3. Packs them with the trimmed LibreOffice and Microsoft's WebView2 offline installer using Inno Setup, and writes
#    the installer's SHA-256 beside it.
# The version comes from Directory.Build.props. Needs .tools\dotnet, .tools\feed with the .NET runtime packs,
# .tools\libreoffice-<version> (scripts\fetch-libreoffice.ps1; -arm64 for ARM64), .tools\innosetup-7.1.0 and .tools\webview2.
# -Stage splits the run for signed release builds (.github/workflows/release.yml): Publish runs steps 1 and 2 and
# copies the app's own programs to artifacts\sign\binaries for signing; after the signed copies are put back into the
# publish folder, Installer runs step 3. The default, All, does everything in one go (unsigned).
param([switch]$SkipTests, [string]$LibreOfficeVersion = '26.2.6', [ValidateSet('All', 'Publish', 'Installer')][string]$Stage = 'All',
  [ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
. "$PSScriptRoot\env.ps1"
$version = ([xml](Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version in Directory.Build.props must look like 1.2.3 (found '$version')." }
$libreOffice = Join-Path $repoRoot (".tools\libreoffice-$LibreOfficeVersion" + $(if ($Arch -eq 'arm64') { '-arm64' } else { '' }))
$iscc = Join-Path $repoRoot '.tools\innosetup-7.1.0\ISCC.exe'
$feed = Join-Path $repoRoot '.tools\feed'
$publish = Join-Path $repoRoot "artifacts\publish\win-$Arch"
$output = Join-Path $repoRoot 'artifacts\installer'
if (-not (Test-Path -LiteralPath (Join-Path $libreOffice 'program\soffice.exe'))) { throw "LibreOffice is missing: run scripts\fetch-libreoffice.ps1 -Arch $Arch." }
if (Test-Path -LiteralPath (Join-Path $libreOffice 'System64')) { throw 'LibreOffice is not trimmed: run scripts\trim-libreoffice.ps1.' }
if (-not (Test-Path -LiteralPath $iscc)) { throw "Inno Setup 7 is missing: expected $iscc" }
# Bundled for PCs without the runtime (DECISIONS.md D9). Must come straight from Microsoft, signed by Microsoft.
$webView2 = Join-Path $repoRoot ('.tools\webview2\MicrosoftEdgeWebView2RuntimeInstaller' + $(if ($Arch -eq 'arm64') { 'ARM64' } else { 'X64' }) + '.exe')
if (-not (Test-Path -LiteralPath $webView2)) { throw 'The WebView2 offline installer is missing: see docs/RELEASING.md, one-time setup step 6.' }
$signature = Get-AuthenticodeSignature -LiteralPath $webView2
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') {
  throw "The WebView2 installer is not validly signed by Microsoft Corporation ($($signature.Status)); download it again from Microsoft."
}

if (-not $SkipTests -and $Stage -ne 'Installer') {
  & "$PSScriptRoot\build.ps1" -Offline
  & "$PSScriptRoot\test.ps1" -Offline
  & "$PSScriptRoot\test-markdown.ps1" -Offline
  & "$PSScriptRoot\test-office-safety.ps1" -Offline
  & "$PSScriptRoot\test-worker.ps1" -Offline
  & "$PSScriptRoot\test-app.ps1" -Offline
  & "$PSScriptRoot\smoke-test.ps1"
  & "$PSScriptRoot\security-smoke.ps1"
}

# The app's own programs: the files release builds have signed (the .NET runtime's files are already signed by
# Microsoft; LibreOffice's and the libraries' files are theirs to sign).
$ownFiles = 'PlainViewer.exe', 'PlainViewer.dll', 'PlainViewer.Core.dll', 'PlainViewer.Worker.exe', 'PlainViewer.Worker.dll'
if ($Stage -ne 'Installer') {
  if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
  # The worker is published last so its own settings files are the ones that remain.
  foreach ($project in 'src\PlainViewer.App\PlainViewer.App.csproj', 'src\PlainViewer.Worker\PlainViewer.Worker.csproj') {
    & $Dotnet publish (Join-Path $repoRoot $project) -c Release -r "win-$Arch" --self-contained true -o $publish --source $feed `
      -p:SatelliteResourceLanguages=en -p:DebugType=none -p:DebugSymbols=false -p:DisableTransitiveFrameworkReferenceDownloads=true
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
  }
  # The installed app carries the .NET runtime, so it also carries the runtime's licence and third-party notices.
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $licenses = Join-Path $publish 'licenses\dotnet'
  New-Item -ItemType Directory -Force -Path $licenses | Out-Null
  $runtimePack = Get-ChildItem -LiteralPath $feed -Filter "microsoft.netcore.app.runtime.win-$Arch.*.nupkg" | Sort-Object Name | Select-Object -Last 1
  $zip = [IO.Compression.ZipFile]::OpenRead($runtimePack.FullName)
  try { foreach ($name in 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') { [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($name), (Join-Path $licenses $name), $true) } }
  finally { $zip.Dispose() }
  foreach ($file in $ownFiles + @('coreclr.dll', 'Assets\prewarm\prewarm-word.docx', 'THIRD-PARTY-NOTICES.md', 'LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Published output is missing $file." }
  }
}
if ($Stage -eq 'Publish') {
  $sign = Join-Path $repoRoot 'artifacts\sign\binaries'
  if (Test-Path -LiteralPath $sign) { Remove-Item -LiteralPath $sign -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $sign | Out-Null
  foreach ($file in $ownFiles) { Copy-Item -LiteralPath (Join-Path $publish $file) -Destination $sign }
  Write-Output "Published; the app's own programs for signing are in $sign"
  return
}
if (-not (Test-Path -LiteralPath (Join-Path $publish 'PlainViewer.exe'))) { throw 'Nothing is published yet: run this script with -Stage Publish first.' }

New-Item -ItemType Directory -Force -Path $output | Out-Null
& $iscc /Qp "/DAppVersion=$version" "/DPublishDir=$publish" "/DLibreOfficeDir=$libreOffice" "/DWebView2Installer=$webView2" "/DOutputDir=$output" "/DArch=$Arch" (Join-Path $repoRoot 'installer\PlainViewer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup could not build the installer.' }
$setup = Join-Path $output "PlainViewer-Setup-$version-$Arch.exe"
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$setup.sha256" -Value "$hash  $(Split-Path $setup -Leaf)" -Encoding ascii
Write-Output "Installer: $setup ($([math]::Round((Get-Item -LiteralPath $setup).Length / 1MB)) MB)"
Write-Output "SHA-256: $hash"
$unsigned = @($ownFiles | Where-Object { (Get-AuthenticodeSignature -LiteralPath (Join-Path $publish $_)).Status -ne 'Valid' })
if ($unsigned.Count -gt 0) { Write-Output 'The programs inside are not code-signed; release builds sign them and the installer through SignPath (docs/RELEASING.md).' }
else { Write-Output 'The programs inside are signed; the installer itself is signed in the next step of the release build.' }
