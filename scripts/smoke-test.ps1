# Opens one or two files of every format in real (off-screen) windows through the worker.
# -App tests an installed or published PlainViewer.exe instead of the development build.
param([string]$App)
if ($App) { $ErrorActionPreference = 'Stop'; $repoRoot = Split-Path $PSScriptRoot -Parent } else { . "$PSScriptRoot\env.ps1" }
. "$PSScriptRoot\app.ps1"
$fixtures = @('simple.txt','complex.txt','simple.csv','complex.csv','simple.md','complex.markdown',
  'pdf\simple.pdf','pdf\complex.pdf','pdf\attack-javascript.pdf','pdf\attack-links.pdf',
  'xlsx\simple.xlsx','xlsx\complex.xlsx','xlsx\drawings.xlsx','xlsx\conditional.xlsx','xlsx\shapes.xlsx','docx\simple.docx','docx\complex-20-pages.docx','pptx\simple.pptx','pptx\complex.pptx',
  'xlsx\variant.xltx','xlsx\macro.xlsm','docx\variant.dotx','docx\macro.docm','pptx\variant.ppsx','pptx\variant.potx','pptx\macro.pptm',
  'images\simple.png','images\complex.png','images\simple.jpg','images\complex-rotated-exif.jpg','images\simple.gif','images\simple.bmp','images\simple.ico',
  'images\simple.webp','images\complex.webp','images\simple.avif','images\complex.avif','images\simple.svg','images\complex.svg','images\png-named.jpg',
  'data\simple.json','data\complex.json','data\simple.xml','data\simple.log','data\simple.ini','data\simple.yaml','data\simple.yml','data\simple.tsv','code\simple.cs','code\simple.sln',
  'web\simple.html','web\simple.xhtml','web\windows-1252.htm','web\simple.mht','web\simple.epub','web\complex.epub',
  'doc\template.dot','odt\template.ott','xls\template.xlt','ods\template.ots','ods\complex.fods','ods\styles.fods',
  'odt\simple.odt','odt\complex.odt','ods\simple.ods','ods\complex.ods','odp\simple.odp','odp\complex.odp','rtf\simple.rtf','rtf\complex.rtf','rtf\rtf-named.doc',
  'doc\simple.doc','doc\complex-20-pages.doc','xls\simple.xls','xls\complex.xls','xls\styles.xls','xls\large-12000-rows.xls','ods\styles.ods','ods\large-12000-rows.ods','xls\drawings.xls','ods\drawings.ods','xls\conditional.xls','ods\conditional.ods','xls\shapes.xls','ods\shapes.ods','ppt\simple.ppt','ppt\complex.ppt','tiff\simple.tif','tiff\scan-3-pages.tiff','heic\jpeg-named.heic') | ForEach-Object { Join-Path $repoRoot "tests\corpus\$_" }
# HEIC photos need Windows' HEIF and HEVC codecs (Microsoft Store). Without them (for example on GitHub's Windows Server
# runners) each photo must be refused with the message naming them.
$heicCodecs = [bool](Get-AppxPackage -Name Microsoft.HEIFImageExtension -ErrorAction SilentlyContinue) -and [bool](Get-AppxPackage -Name Microsoft.HEVCVideoExtension* -ErrorAction SilentlyContinue)
if (-not $heicCodecs) { Write-Output 'HEIC codecs are not installed: HEIC photos are expected to be refused with a message.' }
$fixtures += 'heic\simple.heic', 'heic\complex-rotated.heic', 'heic\large-12mp.heic' | ForEach-Object { $(if ($heicCodecs) { '' } else { '!' }) + (Join-Path $repoRoot "tests\corpus\$_") }
# Password-protected Office files: opened with their test password (no dialog in the smoke test), and refused with a
# clear message when the password is wrong (the app asks again, and the smoke test gives none the second time).
$fixtures += 'xlsx\password.xlsx', 'docx\password.docx', 'pptx\password.pptx' | ForEach-Object { 'password=viewer-test|' + (Join-Path $repoRoot "tests\corpus\$_") }
$fixtures += '!password=wrong|' + (Join-Path $repoRoot 'tests\corpus\xlsx\password.xlsx')
$code = Invoke-PlainViewer $App (@('--smoke-test') + $fixtures)
if ($code -ne 0) { throw 'Native view/worker smoke test failed.' }

# The app's other start-up modes: preparing the converter (run by the installer), exporting a Word file to PDF through
# the viewer's own checks and converter (scripts/compare-office.ps1), and measuring (scripts/measure.ps1).
$prepared = Invoke-PlainViewerOutput $App @('--prepare-converter')
if ($prepared.Code -ne 0) { throw "--prepare-converter failed: $($prepared.Output)" }
Write-Output 'PASS --prepare-converter: the converter is ready'
$pdf = Join-Path ([IO.Path]::GetTempPath()) ("plainviewer-export-" + [Guid]::NewGuid().ToString('N') + '.pdf')
try {
  $exported = Invoke-PlainViewerOutput $App @('--export-pdf', (Join-Path $repoRoot 'tests\corpus\docx\simple.docx'), $pdf)
  $head = if (Test-Path -LiteralPath $pdf) { [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdf), 0, 5) } else { '' }
  if ($exported.Code -ne 0 -or $head -ne '%PDF-') { throw "--export-pdf failed: $($exported.Output)" }
  Write-Output 'PASS --export-pdf: simple.docx became a PDF'
}
finally { if (Test-Path -LiteralPath $pdf) { [IO.File]::Delete($pdf) } }
$measured = Invoke-PlainViewerOutput $App @('--measure', (Join-Path $repoRoot 'tests\corpus\simple.txt'))
$line = ($measured.Output -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
if ($measured.Code -ne 0 -or -not $line -or ($line | ConvertFrom-Json).PSObject.Properties.Name -contains 'error') { throw "--measure failed: $($measured.Output)" }
Write-Output "PASS --measure: $line"
