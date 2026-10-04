; Plain Viewer offline installer (Inno Setup 7). Build it with scripts\package.ps1, which passes the defines below.
; Per-user install, no administrator rights. Upgrades install over the previous version (same AppId).
#ifndef AppVersion
  #error Run scripts\package.ps1: it passes AppVersion, PublishDir, LibreOfficeDir, WebView2Installer and OutputDir.
#endif

[Setup]
; Never change AppId: Windows uses it to recognise upgrades and the uninstaller.
AppId={{3348245C-1DA7-4BEC-8072-123B6E9F1CD0}
AppName=Plain Viewer
AppVersion={#AppVersion}
AppVerName=Plain Viewer {#AppVersion}
VersionInfoVersion={#AppVersion}
; Product name and version as SignPath's signing rules require them on every signed file.
VersionInfoProductName=Plain Viewer
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription=Plain Viewer Setup
DefaultDirName={autopf}\Plain Viewer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 11. Windows 10 is postponed (DECISIONS.md D13): the app does not start on Windows 10 without recent updates.
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=PlainViewer-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
ChangesAssociations=yes
; Close a running Plain Viewer (and its LibreOffice converter, soffice.bin) before files are replaced.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll,*.bin
RestartApplications=no
UninstallDisplayName=Plain Viewer
UninstallDisplayIcon={app}\PlainViewer.exe
SetupIconFile=..\src\PlainViewer.App\Assets\app.ico
SetupLogging=yes
; Users accept these terms, which include Microsoft's terms for the bundled WebView2 Runtime (DECISIONS.md D9).
LicenseFile=terms.txt
; Release builds are signed by SignPath after this script runs (.github/workflows/release.yml, docs/RELEASING.md):
; the app's own programs before they are packed, then this setup program. The uninstaller Inno Setup writes at install
; time is not signed.

[Tasks]
Name: openwith; Description: "Add Plain Viewer to ""Open with"" for PDF, Word, Excel, PowerPoint, OpenDocument, RTF, picture, text, CSV, Markdown and data files (your default apps do not change)"
Name: firewall; Description: "Block the parts that read documents from the network with Windows Firewall (asks for administrator permission once)"
Name: webview2; Description: "Install the Microsoft Edge WebView2 Runtime, which PDF, Word, Excel and PowerPoint files need (included; licensed by Microsoft)"; Check: not WebView2Installed
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrades: remove the previous version's LibreOffice and page files first, so no stale files remain.
Type: filesandordirs; Name: "{app}\libreoffice"
Type: filesandordirs; Name: "{app}\Assets"

[Files]
; Microsoft's signed offline WebView2 installer (package.ps1 checks the signature). Unpacked only when the runtime
; is missing; it is already compressed, and in its own block it unpacks without the rest of the files.
Source: "{#WebView2Installer}"; DestName: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: dontcopy nocompression solidbreak
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#LibreOfficeDir}\*"; DestDir: "{app}\libreoffice"; Excludes: "__pycache__,*.pyc"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Plain Viewer"; Filename: "{app}\PlainViewer.exe"
Name: "{autodesktop}\Plain Viewer"; Filename: "{app}\PlainViewer.exe"; Tasks: desktopicon

[Registry]
; "Open with" and Settings > Default apps. Only offers Plain Viewer; Windows lets the user choose defaults.
Root: HKA; Subkey: "Software\PlainViewer"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\PlainViewer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Plain Viewer"; Tasks: openwith
Root: HKA; Subkey: "Software\PlainViewer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Read-only viewer for PDF, Word, Excel, PowerPoint, OpenDocument, RTF, picture, web page, EPUB, text, CSV, Markdown, code and data files"; Tasks: openwith
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Plain Viewer"; ValueData: "Software\PlainViewer\Capabilities"; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\PlainViewer.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Plain Viewer"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\PlainViewer.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PlainViewer.exe"" ""%1"""; Tasks: openwith
#dim Ext[74]
#define Ext[0] "pdf"
#define Ext[1] "docx"
#define Ext[2] "docm"
#define Ext[3] "dotx"
#define Ext[4] "dotm"
#define Ext[5] "odt"
#define Ext[6] "rtf"
#define Ext[7] "doc"
#define Ext[8] "dot"
#define Ext[9] "ott"
#define Ext[10] "xlsx"
#define Ext[11] "xlsm"
#define Ext[12] "xltx"
#define Ext[13] "xltm"
#define Ext[14] "xlsb"
#define Ext[15] "ods"
#define Ext[16] "xls"
#define Ext[17] "xlt"
#define Ext[18] "ots"
#define Ext[19] "fods"
#define Ext[20] "pptx"
#define Ext[21] "pptm"
#define Ext[22] "potx"
#define Ext[23] "potm"
#define Ext[24] "ppsx"
#define Ext[25] "ppsm"
#define Ext[26] "odp"
#define Ext[27] "ppt"
#define Ext[28] "jpg"
#define Ext[29] "jpeg"
#define Ext[30] "jfif"
#define Ext[31] "png"
#define Ext[32] "gif"
#define Ext[33] "bmp"
#define Ext[34] "ico"
#define Ext[35] "webp"
#define Ext[36] "avif"
#define Ext[37] "svg"
#define Ext[38] "tif"
#define Ext[39] "tiff"
#define Ext[40] "txt"
#define Ext[41] "json"
#define Ext[42] "xml"
#define Ext[43] "log"
#define Ext[44] "ini"
#define Ext[45] "yaml"
#define Ext[46] "yml"
#define Ext[47] "c"
#define Ext[48] "h"
#define Ext[49] "cs"
#define Ext[50] "java"
#define Ext[51] "js"
#define Ext[52] "vb"
#define Ext[53] "css"
#define Ext[54] "php"
#define Ext[55] "asp"
#define Ext[56] "aspx"
#define Ext[57] "razor"
#define Ext[58] "config"
#define Ext[59] "csproj"
#define Ext[60] "sln"
#define Ext[61] "csv"
#define Ext[62] "tsv"
#define Ext[63] "md"
#define Ext[64] "markdown"
#define Ext[65] "heic"
#define Ext[66] "heif"
#define Ext[67] "hif"
#define Ext[68] "htm"
#define Ext[69] "html"
#define Ext[70] "xhtml"
#define Ext[71] "mht"
#define Ext[72] "mhtml"
#define Ext[73] "epub"
#dim Kind[74]
#define Kind[0] "PDF document"
#define Kind[1] "Word document"
#define Kind[2] "Word macro-enabled document"
#define Kind[3] "Word template"
#define Kind[4] "Word macro-enabled template"
#define Kind[5] "OpenDocument text"
#define Kind[6] "Rich Text document"
#define Kind[7] "Word 97-2003 document"
#define Kind[8] "Word 97-2003 template"
#define Kind[9] "OpenDocument text template"
#define Kind[10] "Excel workbook"
#define Kind[11] "Excel macro-enabled workbook"
#define Kind[12] "Excel template"
#define Kind[13] "Excel macro-enabled template"
#define Kind[14] "Excel binary workbook"
#define Kind[15] "OpenDocument spreadsheet"
#define Kind[16] "Excel 97-2003 workbook"
#define Kind[17] "Excel 97-2003 template"
#define Kind[18] "OpenDocument spreadsheet template"
#define Kind[19] "OpenDocument flat spreadsheet"
#define Kind[20] "PowerPoint presentation"
#define Kind[21] "PowerPoint macro-enabled presentation"
#define Kind[22] "PowerPoint template"
#define Kind[23] "PowerPoint macro-enabled template"
#define Kind[24] "PowerPoint show"
#define Kind[25] "PowerPoint macro-enabled show"
#define Kind[26] "OpenDocument presentation"
#define Kind[27] "PowerPoint 97-2003 presentation"
#define Kind[28] "JPEG picture"
#define Kind[29] "JPEG picture"
#define Kind[30] "JPEG picture"
#define Kind[31] "PNG picture"
#define Kind[32] "GIF picture"
#define Kind[33] "Bitmap picture"
#define Kind[34] "Icon"
#define Kind[35] "WebP picture"
#define Kind[36] "AVIF picture"
#define Kind[37] "SVG picture"
#define Kind[38] "TIFF picture"
#define Kind[39] "TIFF picture"
#define Kind[40] "Text document"
#define Kind[41] "JSON file"
#define Kind[42] "XML file"
#define Kind[43] "Log file"
#define Kind[44] "Settings file"
#define Kind[45] "YAML file"
#define Kind[46] "YAML file"
#define Kind[47] "C source file"
#define Kind[48] "C header file"
#define Kind[49] "C# source file"
#define Kind[50] "Java source file"
#define Kind[51] "JavaScript file"
#define Kind[52] "Visual Basic source file"
#define Kind[53] "Style sheet"
#define Kind[54] "PHP file"
#define Kind[55] "ASP page"
#define Kind[56] "ASP.NET page"
#define Kind[57] "Razor file"
#define Kind[58] "Configuration file"
#define Kind[59] "C# project file"
#define Kind[60] "Visual Studio solution"
#define Kind[61] "CSV file"
#define Kind[62] "Tab-separated values file"
#define Kind[63] "Markdown document"
#define Kind[64] "Markdown document"
#define Kind[65] "HEIC photo"
#define Kind[66] "HEIF photo"
#define Kind[67] "HEIF photo"
#define Kind[68] "Web page"
#define Kind[69] "Web page"
#define Kind[70] "Web page"
#define Kind[71] "Web archive"
#define Kind[72] "Web archive"
#define Kind[73] "EPUB book"
#define i
#sub FileType
Root: HKA; Subkey: "Software\Classes\.{#Ext[i]}\OpenWithProgids"; ValueType: string; ValueName: "PlainViewer.{#Ext[i]}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\PlainViewer.{#Ext[i]}"; ValueType: string; ValueName: ""; ValueData: "{#Kind[i]}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\PlainViewer.{#Ext[i]}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\PlainViewer.exe,0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\PlainViewer.{#Ext[i]}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\PlainViewer.exe"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\PlainViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".{#Ext[i]}"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\PlainViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".{#Ext[i]}"; ValueData: "PlainViewer.{#Ext[i]}"; Tasks: openwith
#endsub
#for {i = 0; i < DimOf(Ext); i++} FileType

[Run]
; Builds the Word/PowerPoint converter's profile now, so the first document opens in seconds. If it fails,
; the app repeats it in the background on its next start.
Filename: "{app}\PlainViewer.exe"; Parameters: "--prepare-converter"; StatusMsg: "Preparing the Word and PowerPoint converter..."; Flags: runhidden waituntilterminated
Filename: "{app}\PlainViewer.exe"; Description: "{cm:LaunchProgram,Plain Viewer}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Plain Viewer's private data: converter profile, temporary work folders and the page view's cache.
; Never touches the user's documents.
Type: filesandordirs; Name: "{localappdata}\PlainViewer"
Type: filesandordirs; Name: "{%USERPROFILE}\AppData\LocalLow\PlainViewer"

[Code]
// PDF, Word, Excel and PowerPoint views need the Microsoft Edge WebView2 Runtime. Windows 11 normally includes it,
// but a clean Windows 11 may not, and Windows 10 often lacks it. Registry check documented by Microsoft for the Evergreen runtime.
function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0'))
    or (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0'));
end;

// Optional Windows Firewall rules (DECISIONS.md, gate 1): block the worker and LibreOffice from the network in both
// directions. Adding and removing rules needs administrator rights, so each runs as one elevated command (one prompt).
const
  RulePrefix = 'Plain Viewer - block network - ';

function RuleCommand(Add: Boolean; Name, Exe, Direction: String): String;
begin
  Result := 'netsh advfirewall firewall delete rule name="' + RulePrefix + Name + ' (' + Direction + ')" >nul 2>&1';
  if Add then
    Result := Result + ' & netsh advfirewall firewall add rule name="' + RulePrefix + Name + ' (' + Direction + ')" dir=' + Direction +
      ' action=block program="' + Exe + '" enable=yes profile=any >nul';
end;

function ProgramCommands(Add: Boolean; Name, Exe: String): String;
begin
  Result := RuleCommand(Add, Name, Exe, 'out') + ' & ' + RuleCommand(Add, Name, Exe, 'in');
end;

function FirewallCommand(Add: Boolean): String;
begin
  Result := ProgramCommands(Add, 'document worker', ExpandConstant('{app}\PlainViewer.Worker.exe')) + ' & ' +
    ProgramCommands(Add, 'converter launcher', ExpandConstant('{app}\libreoffice\program\soffice.exe')) + ' & ' +
    ProgramCommands(Add, 'converter', ExpandConstant('{app}\libreoffice\program\soffice.bin')) + ' & ' +
    ProgramCommands(Add, 'converter scripting', ExpandConstant('{app}\libreoffice\program\python.exe'));
end;

procedure RunElevated(Command: String);
var
  ResultCode: Integer;
begin
  // Returns quietly if the user declines the administrator prompt; callers check the rules afterwards.
  ShellExec('runas', ExpandConstant('{cmd}'), '/s /c "' + Command + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function FirewallRulesExist: Boolean;
var
  ResultCode: Integer;
begin
  // Reading rules does not need administrator rights.
  Result := Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="' + RulePrefix + 'converter (out)"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
end;

// Runs Microsoft's offline installer: per-machine when setup is elevated, otherwise for the current user only.
// It has no network access to do without. The runtime then keeps itself up to date and is not removed on uninstall.
procedure InstallWebView2;
var
  ResultCode: Integer;
  Detail: String;
begin
  WizardForm.StatusLabel.Caption := 'Installing the Microsoft Edge WebView2 Runtime...';
  ExtractTemporaryFile('MicrosoftEdgeWebView2RuntimeInstallerX64.exe');
  Detail := '';
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Detail := SysErrorMessage(ResultCode)
  else if ResultCode <> 0 then
    Detail := 'error code ' + IntToStr(ResultCode)
  else
    Detail := 'it reported success, but the runtime is not registered';
  Log('WebView2 Runtime installer finished: ' + IntToStr(ResultCode));
  if not WebView2Installed then
    SuppressibleMsgBox('The Microsoft Edge WebView2 Runtime could not be installed (' + Detail + '). Plain Viewer will open text, CSV and Markdown files, ' +
      'but not PDF, Word, Excel or PowerPoint files. Run this installer again to retry.', mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('webview2') then
    InstallWebView2;
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('firewall') then
  begin
    RunElevated(FirewallCommand(True));
    if not FirewallRulesExist then
      SuppressibleMsgBox('The Windows Firewall rules were not added (administrator permission was not given). Plain Viewer works without them, ' +
        'but the parts that read documents are then not blocked from the network by Windows. Run this installer again to add them.', mbInformation, MB_OK, IDOK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and FirewallRulesExist then
    RunElevated(FirewallCommand(False));
end;

