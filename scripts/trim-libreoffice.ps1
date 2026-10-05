# Prepares the unpacked LibreOffice for shipping inside Plain Viewer:
# - copies Microsoft's C++ runtime DLLs beside soffice.exe (an MSI install would put them in System32; a clean
#   Windows PC may not have them), and
# - removes what a read-only viewer never uses: interface translations, spelling and thesaurus data, Java
#   extensions (no Java is bundled), offline help, icon themes other than the Windows default, and folders
#   that only an MSI install uses.
# Kept on purpose: hyphenation patterns (they change line breaks), fonts, import/export filters, Python (bundled
# dictionary extensions register Python components) and every licence and readme file.
# Run by fetch-libreoffice.ps1. Safe to run again. -Backup moves removed files there instead of deleting them.
param(
  [string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\libreoffice-26.2.6'),
  [string]$Backup
)
$ErrorActionPreference = 'Stop'
$program = Join-Path $Path 'program'
if (-not (Test-Path -LiteralPath (Join-Path $program 'soffice.exe'))) { throw "No LibreOffice found at $Path" }
function Size([string]$folder) { [math]::Round(((Get-ChildItem -LiteralPath $folder -Recurse -File -Force | Measure-Object Length -Sum).Sum) / 1MB) }
$before = Size $Path

# The processor a program or DLL is built for (PE header machine field): 8664 x64, AA64 ARM64, 014C x86.
function Machine([string]$file) {
  $bytes = [IO.File]::ReadAllBytes($file)
  $header = [BitConverter]::ToInt32($bytes, 0x3C)
  '{0:X4}' -f [BitConverter]::ToUInt16($bytes, $header + 4)
}
# The runtime folders of the administrative image hold the C++ runtime for more than one processor; only the one
# matching LibreOffice's own programs is copied.
$machine = Machine (Join-Path $program 'soffice.bin')
foreach ($folder in 'SystemArm64', 'System64', 'System') {
  $runtime = Join-Path $Path $folder
  if ((Test-Path -LiteralPath (Join-Path $runtime 'vcruntime140.dll')) -and (Machine (Join-Path $runtime 'vcruntime140.dll')) -eq $machine) {
    Copy-Item (Join-Path $runtime '*.dll') $program -Force
    break
  }
}
if (-not (Test-Path -LiteralPath (Join-Path $program 'vcruntime140.dll'))) { throw 'The C++ runtime DLLs are missing from LibreOffice''s program folder.' }
# Microsoft's ARM64 set includes an x64 vcruntime140_1.dll (an x64-only exception handler, for programs running
# under x64 emulation); no ARM64 LibreOffice file imports it.
$wrong = @(Get-ChildItem -LiteralPath $program -Filter '*140*.dll' | Where-Object { (Machine $_.FullName) -ne $machine } |
  Where-Object { -not ($machine -eq 'AA64' -and $_.Name -eq 'vcruntime140_1.dll') } | ForEach-Object Name)
if ($wrong) { throw "These C++ runtime DLLs are not built for LibreOffice's processor ($machine): $($wrong -join ', ')." }

# LibreOffice loads its own fonts from share\fonts\truetype; the top-level Fonts folder is a copy meant for C:\Windows\Fonts.
$fonts = Join-Path $Path 'share\fonts\truetype'
if (Test-Path -LiteralPath (Join-Path $Path 'Fonts')) {
  New-Item -ItemType Directory -Force -Path $fonts | Out-Null
  Copy-Item (Join-Path $Path 'Fonts\*') $fonts -Force
}

$remove = [System.Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath (Join-Path $program 'resource') -Directory | Where-Object Name -ne 'common' | ForEach-Object { $remove.Add($_.FullName) }
Get-ChildItem -LiteralPath (Join-Path $Path 'share\extensions') -Directory -Filter 'dict-*' | ForEach-Object {
  Get-ChildItem -LiteralPath $_.FullName -File |
    Where-Object { ($_.Extension -in '.dic', '.aff' -and $_.Name -notlike 'hyph*') -or $_.Name -like 'th_*' -or $_.Name -like 'thes_*' } |
    ForEach-Object { $remove.Add($_.FullName) }
}
Get-ChildItem -LiteralPath (Join-Path $Path 'share\config') -File -Filter 'images_*.zip' | Where-Object Name -notlike 'images_colibre*' | ForEach-Object { $remove.Add($_.FullName) }
foreach ($item in 'share\extensions\nlpsolver', 'share\extensions\wiki-publisher', 'help', 'Fonts', 'System', 'System64', 'SystemArm64') {
  $full = Join-Path $Path $item
  if (Test-Path -LiteralPath $full) { $remove.Add($full) }
}

foreach ($full in $remove) {
  if ($Backup) {
    $destination = Join-Path $Backup $full.Substring($Path.TrimEnd('\').Length + 1)
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Move-Item -LiteralPath $full -Destination $destination -Force
  }
  else { Remove-Item -LiteralPath $full -Recurse -Force }
}
Write-Output "LibreOffice trimmed: $before MB -> $(Size $Path) MB ($($remove.Count) items $(if ($Backup) { "moved to $Backup" } else { 'removed' }))"
