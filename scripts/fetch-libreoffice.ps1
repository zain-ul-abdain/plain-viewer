# Downloads the pinned LibreOffice release, verifies it against the SHA-256 published by The Document Foundation,
# unpacks it (administrative extract, no system-wide install) into .tools\libreoffice-<version> (x64) or
# .tools\libreoffice-<version>-arm64, and trims it for shipping with trim-libreoffice.ps1.
# LibreOffice converts Word and PowerPoint files to PDF for display. It is MPL-2.0 licensed; see THIRD-PARTY-NOTICES.md.
param([string]$Version = '26.2.6', [ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
# Windows Installer refuses paths through a junction ("untrusted mount point"); a worktree's .tools may be one.
$tools = Get-Item -LiteralPath (Join-Path $repoRoot '.tools')
$tools = if ($tools.LinkType -eq 'Junction') { @($tools.Target)[0] } else { $tools.FullName }
$target = Join-Path $tools ("libreoffice-$Version" + $(if ($Arch -eq 'arm64') { '-arm64' } else { '' }))
if (Test-Path (Join-Path $target 'program\soffice.exe')) { Write-Output "LibreOffice $Version is already at $target"; return }
$downloads = Join-Path $tools 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$folder, $suffix = if ($Arch -eq 'arm64') { 'aarch64', 'aarch64' } else { 'x86_64', 'x86-64' }
$name = "LibreOffice_${Version}_Win_$suffix.msi"
$msi = Join-Path $downloads $name
$url = "https://download.documentfoundation.org/libreoffice/stable/$Version/win/$folder/$name"

# Node's fetch is used because Windows PowerShell's web client fails TLS in some sandboxed accounts.
$download = @'
const fs = require("fs"), crypto = require("crypto");
(async () => {
  const [url, file] = process.argv.slice(2);
  const expected = (await (await fetch(url + ".sha256", { redirect: "error" })).text()).trim().split(/\s+/)[0];
  if (!/^[0-9a-f]{64}$/.test(expected)) throw new Error("no checksum from download.documentfoundation.org");
  // A copy downloaded earlier is used again if it still matches the published checksum.
  if (fs.existsSync(file) && crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex") === expected) { console.log("Verified SHA-256 " + expected + " (already downloaded)"); return; }
  const response = await fetch(url);
  if (!response.ok) throw new Error("HTTP " + response.status);
  const hash = crypto.createHash("sha256"), out = fs.createWriteStream(file + ".part");
  for await (const chunk of response.body) { hash.update(chunk); out.write(chunk); }
  await new Promise(resolve => out.end(resolve));
  const actual = hash.digest("hex");
  if (actual !== expected) { fs.unlinkSync(file + ".part"); throw new Error(`checksum mismatch: expected ${expected}, got ${actual}`); }
  fs.renameSync(file + ".part", file);
  console.log("Verified SHA-256 " + actual);
})().catch(e => { console.error(e.message); process.exit(1); });
'@
# Run from a file: Windows PowerShell 5.1 drops the double quotes inside a script passed with node -e.
$script = Join-Path $downloads 'fetch-checked.js'
Set-Content -LiteralPath $script -Value $download -Encoding utf8
& node $script $url $msi
# Older releases move from "stable" to The Document Foundation's archive once a newer one is out.
if ($LASTEXITCODE -ne 0) { & node $script "https://downloadarchive.documentfoundation.org/libreoffice/old/$Version/win/$folder/$name" $msi }
if ($LASTEXITCODE -ne 0) { throw 'Download or checksum verification failed.' }

$process = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$target`"") -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Unpacking failed (msiexec exit code $($process.ExitCode))." }

# The ARM64 package carries the C++ runtime twice in the same folder, for x64 and ARM64 PCs, chosen by a condition
# when installing; an administrative extract keeps the x64 copies. The ARM64 ones (DLL file keys ending in .dll_arm64.<id>)
# are taken from the package's cabinet into SystemArm64, which trim-libreoffice.ps1 uses.
if ($Arch -eq 'arm64') {
  Add-Type -TypeDefinition @'
using System; using System.IO; using System.Runtime.InteropServices;
public static class MsiCabinet {
  [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr db);
  [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
  [DllImport("msi.dll")] static extern uint MsiViewExecute(IntPtr view, IntPtr record);
  [DllImport("msi.dll")] static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
  [DllImport("msi.dll")] static extern uint MsiRecordReadStream(IntPtr record, uint field, byte[] buffer, ref uint size);
  [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr handle);
  public static void Save(string msi, string stream, string file) {
    IntPtr db, view, record; uint r;
    if ((r = MsiOpenDatabaseW(msi, IntPtr.Zero, out db)) != 0) throw new Exception("MsiOpenDatabase " + r);
    try {
      if ((r = MsiDatabaseOpenViewW(db, "SELECT `Data` FROM `_Streams` WHERE `Name`='" + stream + "'", out view)) != 0) throw new Exception("MsiDatabaseOpenView " + r);
      MsiViewExecute(view, IntPtr.Zero);
      if ((r = MsiViewFetch(view, out record)) != 0) throw new Exception("No stream " + stream);
      var buffer = new byte[1 << 20];
      using (var output = File.Create(file))
        while (true) { uint size = (uint)buffer.Length; if ((r = MsiRecordReadStream(record, 1, buffer, ref size)) != 0) throw new Exception("MsiRecordReadStream " + r); if (size == 0) break; output.Write(buffer, 0, (int)size); }
      MsiCloseHandle(record); MsiCloseHandle(view);
    }
    finally { MsiCloseHandle(db); }
  }
}
'@
  $cabinet = Join-Path $downloads 'libreoffice1.cab'
  [MsiCabinet]::Save($msi, 'libreoffice1.cab', $cabinet)
  $extracted = Join-Path $downloads 'arm64-runtime'
  New-Item -ItemType Directory -Force -Path $extracted | Out-Null
  & expand.exe $cabinet '-F:*.dll_arm64.*' $extracted | Out-Null
  $runtime = Join-Path $target 'SystemArm64'
  New-Item -ItemType Directory -Force -Path $runtime | Out-Null
  foreach ($file in Get-ChildItem -LiteralPath $extracted -File) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') { throw "$($file.Name) is not validly signed by Microsoft." }
    Move-Item -LiteralPath $file.FullName -Destination (Join-Path $runtime $file.Name.Substring(0, $file.Name.IndexOf('_arm64.'))) -Force
  }
  Remove-Item -LiteralPath $cabinet -Force
  Remove-Item -LiteralPath $extracted -Recurse -Force
}
Remove-Item $msi -Force
Remove-Item $script -Force
Remove-Item (Join-Path $target $name) -Force -ErrorAction SilentlyContinue

# Moves the fonts where LibreOffice loads them, adds the C++ runtime and removes what the viewer never uses.
& (Join-Path $PSScriptRoot 'trim-libreoffice.ps1') -Path $target
Write-Output "LibreOffice $Version unpacked to $target"
