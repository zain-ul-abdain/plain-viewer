# Makes the Word, Excel and PowerPoint 97-2003 test fixtures (tests/corpus/doc, xls, ppt) by converting this corpus's
# own .docx, .xlsx and .pptx fixtures with the development LibreOffice. No independent producer of these binary
# formats is available, so they prove the viewer's safety checks and behaviour, not its rendering fidelity. They are
# kept in git; existing files are only replaced with -Force (LibreOffice writes a new timestamp every time).
# Afterwards run `node generate.mjs` in tests/corpus/generate to record them in manifest.json.
param([switch]$Force)
. "$PSScriptRoot\env.ps1"
$corpus = Join-Path $repoRoot 'tests\corpus'
$soffice = Get-ChildItem (Join-Path $repoRoot '.tools') -Directory -Filter 'libreoffice-*' | Sort-Object Name -Descending |
  ForEach-Object { Join-Path $_.FullName 'program\soffice.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $soffice) { throw 'LibreOffice is missing: run scripts\fetch-libreoffice.ps1.' }

# A throwaway profile: macros off, links never updated, and every web request sent to a closed port, because some
# sources are hostile fixtures that point at the test listener.
$work = Join-Path ([IO.Path]::GetTempPath()) ("plainviewer-legacy-" + [Guid]::NewGuid().ToString('N'))
$user = Join-Path $work 'profile\user'; New-Item -ItemType Directory -Force $user | Out-Null
$items = @(
  @('/org.openoffice.Office.Common/Security/Scripting', 'MacroSecurityLevel', '3'), @('/org.openoffice.Office.Common/Security/Scripting', 'DisableMacrosExecution', 'true'),
  @('/org.openoffice.Office.Writer/Content/Update', 'Link', '2'), @('/org.openoffice.Office.Writer/Content/Update', 'Field', 'false'), @('/org.openoffice.Office.Calc/Content/Update', 'Link', '1'),
  @('/org.openoffice.Inet/Settings', 'ooInetProxyType', '2'), @('/org.openoffice.Inet/Settings', 'ooInetHTTPProxyName', '127.0.0.1'), @('/org.openoffice.Inet/Settings', 'ooInetHTTPProxyPort', '9'),
  @('/org.openoffice.Inet/Settings', 'ooInetHTTPSProxyName', '127.0.0.1'), @('/org.openoffice.Inet/Settings', 'ooInetHTTPSProxyPort', '9'), @('/org.openoffice.Inet/Settings', 'ooInetNoProxy', ''))
$xml = '<?xml version="1.0" encoding="UTF-8"?><oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema">' +
  (($items | ForEach-Object { "<item oor:path=`"$($_[0])`"><prop oor:name=`"$($_[1])`" oor:op=`"fuse`"><value>$($_[2])</value></prop></item>" }) -join '') + '</oor:items>'
Set-Content -LiteralPath (Join-Path $user 'registrymodifications.xcu') -Value $xml -Encoding utf8
$profileUrl = 'file:///' + (Join-Path $work 'profile').Replace('\', '/')

$jobs = @(
  @('docx\simple.docx', 'doc\simple.doc'), @('docx\complex-20-pages.docx', 'doc\complex-20-pages.doc'),
  @('docx\attack-remote-image.docx', 'doc\attack-remote-image.doc'), @('docx\attack-unc-image.docx', 'doc\attack-unc-image.doc'),
  @('xlsx\simple.xlsx', 'xls\simple.xls'), @('xlsx\complex.xlsx', 'xls\complex.xls'), @('xlsx\styles.xlsx', 'xls\styles.xls'), @('ods\large-12000-rows.ods', 'xls\large-12000-rows.xls'),
  @('xlsx\drawings.xlsx', 'xls\drawings.xls'), @('xlsx\drawings.xlsx', 'ods\drawings.ods'),
  @('xlsx\conditional.xlsx', 'xls\conditional.xls'), @('xlsx\conditional.xlsx', 'ods\conditional.ods'), @('xlsx\styles.xlsx', 'ods\styles-libreoffice.ods'), @('xlsx\shapes.xlsx', 'ods\shapes.ods'), @('xlsx\shapes.xlsx', 'xls\shapes.xls'),
  @('pptx\simple.pptx', 'ppt\simple.ppt'), @('pptx\complex.pptx', 'ppt\complex.ppt'), @('pptx\attack-remote-image.pptx', 'ppt\attack-remote-image.ppt'),
  # Templates and the flat OpenDocument spreadsheet (0.8.0); the third item is LibreOffice's export filter.
  @('docx\simple.docx', 'doc\template.dot', 'MS Word 97 Vorlage'), @('docx\simple.docx', 'odt\template.ott', 'writer8_template'),
  @('xlsx\complex.xlsx', 'xls\template.xlt', 'MS Excel 97 Vorlage/Template'), @('xlsx\complex.xlsx', 'ods\template.ots', 'calc8_template'),
  @('xlsx\complex.xlsx', 'ods\complex.fods', 'OpenDocument Spreadsheet Flat XML'), @('xlsx\styles.xlsx', 'ods\styles.fods', 'OpenDocument Spreadsheet Flat XML'),
  # Windows metafiles drawn by LibreOffice from the corpus's SVG picture (after 0.9.0); the corpus generator puts the
  # EMF into xlsx\metafiles.xlsx, whose .xls and .ods copies come from the line after.
  @('images\complex.svg', 'media\drawing.emf', 'draw_emf_Export'), @('images\complex.svg', 'media\drawing.wmf', 'draw_wmf_Export'),
  @('xlsx\metafiles.xlsx', 'xls\metafiles.xls'), @('xlsx\metafiles.xlsx', 'ods\metafiles.ods'))
try {
  foreach ($job in $jobs) {
    $target = Join-Path $corpus $job[1]
    if ((Test-Path $target) -and -not $Force) { Write-Output "kept    $($job[1])"; continue }
    if (-not (Test-Path (Join-Path $corpus $job[0]))) { Write-Output "skipped $($job[1]): run the corpus generator first, then this script again"; continue }
    $format = [IO.Path]::GetExtension($target).TrimStart('.')
    $out = Join-Path $work 'out'; New-Item -ItemType Directory -Force $out, (Split-Path $target) | Out-Null
    $process = Start-Process $soffice -Wait -PassThru -WindowStyle Hidden -ArgumentList @('--headless', '--norestore', '--nologo', '--nolockcheck',
      "`"-env:UserInstallation=$profileUrl`"", '--convert-to', $(if ($job.Count -gt 2) { "`"$($format):$($job[2])`"" } else { $format }), '--outdir', "`"$out`"", "`"$(Join-Path $corpus $job[0])`"")
    $made = Join-Path $out ([IO.Path]::GetFileNameWithoutExtension($job[0]) + ".$format")
    if ($process.ExitCode -ne 0 -or -not (Test-Path $made)) { throw "LibreOffice could not make $($job[1])." }
    Move-Item -LiteralPath $made -Destination $target -Force
    Write-Output "made    $($job[1])"
  }
}
finally { [IO.Directory]::Delete($work, $true) }
