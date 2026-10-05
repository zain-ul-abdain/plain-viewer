# Makes the password-protected Word and Excel 97-2003 and OpenDocument test fixtures by saving this
# corpus's own documents with a password in the development LibreOffice (through UNO, make-protected-office.py). The
# password is the corpus's test password, "viewer-test". They prove the viewer's decryption, not rendering fidelity, and
# are kept in git; existing files are only replaced with -Force (every save has a new random salt).
# Afterwards run `node generate.mjs` in tests/corpus/generate to record them in manifest.json.
param([switch]$Force)
. "$PSScriptRoot\env.ps1"
$corpus = Join-Path $repoRoot 'tests\corpus'
$program = Get-ChildItem (Join-Path $repoRoot '.tools') -Directory -Filter 'libreoffice-*' | Where-Object Name -notlike '*-arm64' | Sort-Object Name -Descending |
  ForEach-Object { Join-Path $_.FullName 'program' } | Where-Object { Test-Path (Join-Path $_ 'soffice.exe') } | Select-Object -First 1
if (-not $program) { throw 'LibreOffice is missing: run scripts\fetch-libreoffice.ps1.' }

# The current default: .doc and .xls with RC4 encryption; OpenDocument (ODF 1.4) with the whole package encrypted,
# Argon2id and AES-256-GCM.
$current = @(
  @('docx\simple.docx', 'doc\password.doc', 'MS Word 97'),
  @('xlsx\simple.xlsx', 'xls\password.xls', 'MS Excel 97'),
  # No .ppt: LibreOffice saves PowerPoint 97-2003 files without encryption even when given a password.
  @('docx\simple.docx', 'odt\password-libreoffice.odt', 'writer8'),
  @('xlsx\simple.xlsx', 'ods\password.ods', 'calc8'),
  @('pptx\simple.pptx', 'odp\password.odp', 'impress8'))
# ODF 1.2 (the "DefaultVersion" setting 9): each part encrypted on its own with PBKDF2 and AES-256-CBC, as older
# LibreOffice and other producers write.
$classic = @(
  @('docx\simple.docx', 'odt\password-odf12.odt', 'writer8'),
  @('xlsx\simple.xlsx', 'ods\password-odf12.ods', 'calc8'))

# Excel's fixed password, "VelvetSweatshop", which Excel uses for workbooks protected without a password to open:
# the viewer tries it before asking.
$default = @(
  @('xlsx\simple.xlsx', 'xls\password-default.xls', 'MS Excel 97'),
  @('xlsx\simple.xlsx', 'xlsx\password-default.xlsx', 'Calc MS Excel 2007 XML'))

# Saves the jobs in one LibreOffice run with a throwaway profile: macros off, links never updated, every web request
# sent to a closed port, plus the extra settings given.
function Save-Protected($jobs, $extra) {
  $jobs = @($jobs | Where-Object { $Force -or -not (Test-Path (Join-Path $corpus $_[1])) })
  if (-not $jobs) { return }
  $work = Join-Path ([IO.Path]::GetTempPath()) ("plainviewer-protected-" + [Guid]::NewGuid().ToString('N'))
  $user = Join-Path $work 'profile\user'; New-Item -ItemType Directory -Force $user | Out-Null
  $items = @(
    @('/org.openoffice.Office.Common/Security/Scripting', 'MacroSecurityLevel', '3'), @('/org.openoffice.Office.Common/Security/Scripting', 'DisableMacrosExecution', 'true'),
    @('/org.openoffice.Office.Writer/Content/Update', 'Link', '2'), @('/org.openoffice.Office.Writer/Content/Update', 'Field', 'false'), @('/org.openoffice.Office.Calc/Content/Update', 'Link', '1'),
    @('/org.openoffice.Inet/Settings', 'ooInetProxyType', '2'), @('/org.openoffice.Inet/Settings', 'ooInetHTTPProxyName', '127.0.0.1'), @('/org.openoffice.Inet/Settings', 'ooInetHTTPProxyPort', '9'),
    @('/org.openoffice.Inet/Settings', 'ooInetHTTPSProxyName', '127.0.0.1'), @('/org.openoffice.Inet/Settings', 'ooInetHTTPSProxyPort', '9'), @('/org.openoffice.Inet/Settings', 'ooInetNoProxy', '')) + $extra
  $xml = '<?xml version="1.0" encoding="UTF-8"?><oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema">' +
    (($items | ForEach-Object { "<item oor:path=`"$($_[0])`"><prop oor:name=`"$($_[1])`" oor:op=`"fuse`"><value>$($_[2])</value></prop></item>" }) -join '') + '</oor:items>'
  Set-Content -LiteralPath (Join-Path $user 'registrymodifications.xcu') -Value $xml -Encoding utf8
  $profileUrl = 'file:///' + (Join-Path $work 'profile').Replace('\', '/')
  $pipe = 'plainviewer' + [Guid]::NewGuid().ToString('N')
  $office = Start-Process (Join-Path $program 'soffice.exe') -PassThru -WindowStyle Hidden -ArgumentList @('--headless', '--norestore', '--nologo', '--nolockcheck',
    "`"-env:UserInstallation=$profileUrl`"", "`"--accept=pipe,name=$pipe;urp;`"")
  try {
    $arguments = @((Join-Path $PSScriptRoot 'make-protected-office.py'), $pipe)
    foreach ($job in $jobs) {
      New-Item -ItemType Directory -Force (Split-Path (Join-Path $corpus $job[1])) | Out-Null
      $arguments += (Join-Path $corpus $job[0]), (Join-Path $corpus $job[1]), $job[2]
    }
    & (Join-Path $program 'python.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'LibreOffice could not save the protected fixtures.' }
  }
  finally {
    if (-not $office.WaitForExit(30000)) { $office | Stop-Process -Force }
    Start-Sleep -Milliseconds 500
    [IO.Directory]::Delete($work, $true)
  }
}

Save-Protected $current @()
Save-Protected $classic @(, @('/org.openoffice.Office.Common/Save/ODF', 'DefaultVersion', '9'))
$env:PLAINVIEWER_FIXTURE_PASSWORD = 'VelvetSweatshop'
try { Save-Protected $default @() } finally { Remove-Item Env:PLAINVIEWER_FIXTURE_PASSWORD }
