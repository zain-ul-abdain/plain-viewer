# Prepares the build tools on a clean GitHub-hosted Windows runner, in the same places a developer PC has them
# (docs/RELEASING.md, one-time setup), so scripts\package.ps1 runs unchanged. Used by .github/workflows/release.yml;
# not needed on a PC that is already set up. Every download is checked before it is used:
# - the NuGet packages of the offline feed against the SHA-256 of the copies the releases so far were built with;
# - Inno Setup against the digest GitHub shows for the official release asset, and its Authenticode signature;
# - LibreOffice by scripts\fetch-libreoffice.ps1 (SHA-256 published by The Document Foundation);
# - the WebView2 offline installer by scripts\package.ps1 (valid signature by Microsoft Corporation).
# The .NET SDK itself comes from actions/setup-dotnet (pinned in the workflow).
# -Arch arm64 fetches the ARM64 runtime packs, LibreOffice and WebView2 installer instead of the x64 ones.
param([ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $repoRoot '.tools'
$downloads = Join-Path $tools 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null

function Get-Checked([string]$url, [string]$file, [string]$sha256) {
  Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
  $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $sha256) { Remove-Item -LiteralPath $file -Force; throw "Checksum mismatch for $url (expected $sha256, got $actual)." }
  Write-Output "Verified $(Split-Path $file -Leaf)"
}

# 1. The offline NuGet feed (the packages the projects restore, and the .NET runtime packs the app ships with).
$feed = Join-Path $tools 'feed'
New-Item -ItemType Directory -Force -Path $feed | Out-Null
$packages = @(
  @('excelnumberformat', '1.1.0', 'c48af3056098d8f123225b69c16d814f58ff28928e1f6a8fb6fba65fe35366f1'),
  @('markdig', '1.4.0', 'bdd84353a3f499f989f3111b33aceebd9434f8a069ca8b3da2a065f2230e7baf'),
  @('microsoft.netcore.app.runtime.win-x64', '10.0.12', '0f63dee7ca4383cb16848966f81e439399e2058e1671ebe27e5eaf4b200e8691'),
  @('microsoft.web.webview2', '1.0.4191.47', 'f492bbf547d0da329553b6727435b677579b1e9f91cc9e4a1ad029366d5f23d0'),
  @('microsoft.windowsdesktop.app.runtime.win-x64', '10.0.12', '68bce56d2402969d82ac77fbd2b2a54f55fda86ea060366e6ddb832353dab80a'))
# ARM64 (5 Oct 2026): checked with `dotnet nuget verify --all` (Microsoft Corporation and nuget.org signatures).
if ($Arch -eq 'arm64') {
  $packages = @($packages | Where-Object { $_[0] -notlike '*.runtime.win-x64' }) + @(
    , @('microsoft.netcore.app.host.win-arm64', '10.0.12', 'f8c171a5278c5a049dc50d97ea1c71778f8a335c2d19be49bb68dbb1dc922966')
    , @('microsoft.netcore.app.runtime.win-arm64', '10.0.12', 'a684e7ec6acbc2d27dd6926bc11cb12ef1cbe11f1e92f9fbb9553ca7bd00db68')
    , @('microsoft.windowsdesktop.app.runtime.win-arm64', '10.0.12', '2d20cd8f7f432dec32fb942f68dbff4e059b27c57ad40c845e57f95af6cf9ac8'))
}
foreach ($package in $packages) {
  $name, $version, $sha = $package
  Get-Checked "https://api.nuget.org/v3-flatcontainer/$name/$version/$name.$version.nupkg" (Join-Path $feed "$name.$version.nupkg") $sha
}

# 2. Inno Setup 7.1.0, installed for the current user only (as on the developer PC).
$inno = Join-Path $tools 'innosetup-7.1.0'
if (-not (Test-Path -LiteralPath (Join-Path $inno 'ISCC.exe'))) {
  $setup = Join-Path $downloads 'innosetup-7.1.0-x64.exe'
  Get-Checked 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' $setup '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
  $signature = Get-AuthenticodeSignature -LiteralPath $setup
  if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Pyrsys B.V.,*') {
    throw "The Inno Setup installer is not validly signed by its publisher, Pyrsys B.V. ($($signature.Status), $($signature.SignerCertificate.Subject))."
  }
  Write-Output "Inno Setup signed by: $($signature.SignerCertificate.Subject)"
  $process = Start-Process -FilePath $setup -Wait -PassThru -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS',
    '/MERGETASKS="!desktopicon,!fileassoc"', "/DIR=`"$inno`"")
  if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $inno 'ISCC.exe'))) { throw "Inno Setup did not install (exit code $($process.ExitCode))." }
}

# 3. LibreOffice, downloaded, verified, unpacked and trimmed.
& (Join-Path $PSScriptRoot 'fetch-libreoffice.ps1') -Arch $Arch

# 4. Microsoft's WebView2 offline ("Evergreen Standalone") installer for x64, bundled for PCs without the runtime
#    (DECISIONS.md D9). This link is the one Microsoft's WebView2 download page gives for it.
$webView2 = Join-Path $tools 'webview2'
New-Item -ItemType Directory -Force -Path $webView2 | Out-Null
# ARM64: linkid=2099616, the ARM64 link on the same page.
if ($Arch -eq 'arm64') { Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=2099616' -OutFile (Join-Path $webView2 'MicrosoftEdgeWebView2RuntimeInstallerARM64.exe') -UseBasicParsing }
else { Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=2124701' -OutFile (Join-Path $webView2 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe') -UseBasicParsing }
Write-Output 'Build tools ready.'
