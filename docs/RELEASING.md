# Releasing and updating Plain Viewer

## What the installer is

`scripts/package.ps1` builds one file, `artifacts/installer/PlainViewer-Setup-<version>-x64.exe`, plus a `.sha256` file beside it (`-WithoutWebView2` and `-Arch arm64` build the variants described below).

- Installs for the current user only, without administrator rights, into `%LOCALAPPDATA%\Programs\Plain Viewer`. Windows 11, x64. Windows 10 is postponed (DECISIONS.md D13); `scripts\win10-vm-test.ps1` tests it in a Hyper-V virtual machine when that work resumes.
- Contains everything needed offline: the app and its worker with the .NET 10 runtime included, PDF.js, a trimmed LibreOffice 26.2.6 for Word and PowerPoint files, and Microsoft's offline WebView2 Runtime installer (DECISIONS.md D9). Windows 11 normally includes the WebView2 Runtime, but a clean Windows 11 may not; setup then offers the task "Install the Microsoft Edge WebView2 Runtime" (on by default) and runs Microsoft's installer. Without the runtime the app opens only text, CSV and Markdown files and explains why for the others. 368 MB since 5 Oct 2026 (403 MB before LibreOffice's translated configuration, gallery, templates, wizards, PDF import, Java class files and extra icon sets were trimmed: LibreOffice 728 MB -> 581 MB unpacked).
- **Smaller variant without WebView2:** `package.ps1 -WithoutWebView2` builds `PlainViewer-Setup-<version>-x64-without-webview2.exe` (166 MB): the same app without Microsoft's 202 MB WebView2 installer, for PCs that already have the runtime (Windows 11 normally does). It shows `installer/terms-without-webview2.txt` and, when the runtime is missing, says at the end that PDF, Word, Excel and PowerPoint files need the full installer. Which installers to publish is Zain's choice (D9 chose bundling); offering both lets most users download less than half.
- Shows a terms page (`installer/terms.txt`) that users accept; installing the WebView2 Runtime makes it subject to Microsoft's own licence terms.
- Adds a Start menu entry, an uninstaller in Settings > Apps, and (optional, on by default) "Open with" entries for .pdf, .docx, .xlsx, .pptx, .csv, .txt, .md and .markdown. It never changes the user's default apps; Windows lets the user pick Plain Viewer as the default in Settings > Default apps.
- Runs `PlainViewer.exe --prepare-converter` at the end, which builds LibreOffice's private profile so the first Word or PowerPoint file opens in seconds.
- Uninstalling leaves the WebView2 Runtime (a shared Windows component) and removes the app, its private data in `%LOCALAPPDATA%\PlainViewer` (the document view's WebView2 data) and `%USERPROFILE%\AppData\LocalLow\PlainViewer` (converter profile, temporary work folders), and the firewall rules if they were added. It never touches the user's documents.

## One-time setup on a build PC

Everything lives under `.tools` (not in Git):

1. .NET SDK in `.tools\dotnet` (version per `global.json`).
2. Offline NuGet feed `.tools\feed`: the packages the projects use, plus the runtime packs `microsoft.netcore.app.runtime.win-x64` and `microsoft.windowsdesktop.app.runtime.win-x64` in the version the SDK bundles (10.0.12 for SDK 10.0.401). Download them from `https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.<version>.nupkg` and check them with `dotnet nuget verify --all <file>` (must report Microsoft Corporation and nuget.org signatures).
3. LibreOffice: `.\scripts\fetch-libreoffice.ps1` downloads the pinned version, checks its SHA-256 against download.documentfoundation.org, unpacks it and trims it (`trim-libreoffice.ps1`).
4. Inno Setup 7.1.0 in `.tools\innosetup-7.1.0`: download `innosetup-7.1.0-x64.exe` from the official GitHub release linked on https://jrsoftware.org/isdl.php, compare its SHA-256 with the digest on the release page, check its signature, then install it for the current user only:
   `innosetup-7.1.0-x64.exe /VERYSILENT /CURRENTUSER /NOICONS /MERGETASKS="!desktopicon,!fileassoc" /DIR="<repo>\.tools\innosetup-7.1.0"`
5. Node.js (for the test corpus generator and the security smoke test's request listener).
6. Microsoft's WebView2 "Evergreen Standalone Installer" (x64) in `.tools\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe`, from https://developer.microsoft.com/microsoft-edge/webview2 (the download requires accepting Microsoft's WebView2 Runtime licence terms; Zain accepted them and chose to bundle the runtime on 28 Sep 2026). `package.ps1` refuses it unless `Get-AuthenticodeSignature` reports a valid signature by Microsoft Corporation.

## ARM64 installer (built, untested)

`.\scripts\package.ps1 -Arch arm64` builds `artifacts\installer\PlainViewer-Setup-<version>-arm64.exe`, a native ARM64 installer (Inno Setup `ArchitecturesAllowed=arm64`: it refuses x64 PCs). The x64 installer also installs on ARM64 PCs and runs under Windows' x64 emulation. One-time setup, in addition to the x64 items above (Zain approved these downloads on 5 Oct 2026):

- `.tools\feed`: `microsoft.netcore.app.host.win-arm64`, `microsoft.netcore.app.runtime.win-arm64` and `microsoft.windowsdesktop.app.runtime.win-arm64`, version 10.0.12, checked with `dotnet nuget verify --all` (their SHA-256 values are pinned in `scripts/ci-prepare.ps1 -Arch arm64`).
- `.\scripts\fetch-libreoffice.ps1 -Arch arm64`: LibreOffice's `Win_aarch64` MSI, checked against The Document Foundation's SHA-256, unpacked to `.tools\libreoffice-<version>-arm64` and trimmed. That MSI holds the C++ runtime for both x64 and ARM64 in the same folder (chosen by a condition when installing), so an administrative extract keeps the x64 copies; the script takes the ARM64 copies from the package's cabinet and checks Microsoft's signature, and `trim-libreoffice.ps1` refuses runtime DLLs that do not match `soffice.bin`'s processor (Microsoft's ARM64 set includes an x64 `vcruntime140_1.dll`, which no ARM64 file imports). Windows Installer cannot read a package through a junction (a worktree's `.tools` may be one), so the script works in the junction's target.
- `.tools\webview2\MicrosoftEdgeWebView2RuntimeInstallerARM64.exe`: https://go.microsoft.com/fwlink/?linkid=2099616 (the ARM64 link on Microsoft's WebView2 page); `package.ps1` checks Microsoft's signature as for x64.

Checked on this x64 PC (5 Oct 2026): every native program and DLL in the published app is ARM64 (PE machine AA64), LibreOffice's programs and C++ runtime are ARM64, the installer builds (393 MB, x64 one 403 MB). **Not tested:** installing or running it, which needs an ARM64 PC. The release workflow still builds x64 only.

## Making a release

1. Change `<Version>` in `Directory.Build.props` (for example 0.1.0 → 0.1.1 for fixes and security updates, 0.2.0 for new features). The version appears in About, in the installer's name and in Settings > Apps.
2. Update bundled components if needed (next section).
3. Run `.\scripts\package.ps1`. It runs the build, core tests, Markdown tests, Office safety tests, smoke test and security smoke test, then publishes and builds the installer. It stops at the first failure.
4. Test the installer on a clean PC before publishing it: `.\scripts\sandbox-test.ps1` (needs the Windows Sandbox feature and about 8 GB free on C:, because the sandbox's disk lives there and a run uses about 7 GB until the sandbox closes; takes about 10 minutes). It installs the newest installer in a throwaway Windows 11 with networking off, first without the WebView2 task (checking the app's message), then again as an upgrade with the bundled WebView2 Runtime; it checks the converter profile, firewall rules and "Open with", opens and refuses the test files, checks keyboard use and high contrast, uninstalls and checks that nothing is left. Results are in `artifacts\sandbox-test\<run>\results`. A quicker check on the build PC itself:

       $setup = '.\artifacts\installer\PlainViewer-Setup-<version>-x64.exe'
       $dir = "$env:LOCALAPPDATA\PlainViewerInstallTest"
       Start-Process $setup -Wait -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NOICONS','/MERGETASKS="!openwith,!desktopicon"',"/DIR=`"$dir`""
       .\scripts\smoke-test.ps1 -App "$dir\PlainViewer.exe"
       .\scripts\security-smoke.ps1 -App "$dir\PlainViewer.exe"
       Start-Process "$dir\unins000.exe" -Wait -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES'

5. For a signed installer, build it in GitHub Actions instead of on this PC (next section): signing through SignPath only accepts builds made by the repository's own workflow on GitHub's machines.
6. Publish the installer, its `.sha256` file and short release notes (add a section to `docs/RELEASE-NOTES.md`). Keep earlier installers. For the private beta, testers also get `docs/BETA.md` (guide) and `docs/BETA-FEEDBACK.md` (problem report form).

Never change `AppId` in `installer/PlainViewer.iss`; it is how Windows recognises the same app for upgrades.

## How users update

Users download and run the newer installer. It installs over the old version: it closes Plain Viewer if it is running, replaces the program files (the previous LibreOffice and page files are removed first so nothing stale remains) and keeps the "Open with" choices. If LibreOffice changed, the converter profile is rebuilt automatically. Nothing needs to be uninstalled first.

The app never checks for updates by itself, because it must not go online. If update notices are wanted later, the most that fits the specification is an About-box link that opens the download page in the browser after asking.

## When to release

Plain Viewer opens files from strangers, so ship an update whenever a bundled component fixes a security problem:

| Component | Where to watch | How to update |
|---|---|---|
| .NET runtime (monthly, on Patch Tuesday) | https://dotnet.microsoft.com/download/dotnet/10.0 | Install the new SDK into `.tools\dotnet`, put the matching runtime packs in `.tools\feed` |
| LibreOffice | https://www.libreoffice.org/about-us/security/advisories/ | `fetch-libreoffice.ps1 -Version <new>`, then `package.ps1 -LibreOfficeVersion <new>`; update THIRD-PARTY-NOTICES.md (version, checksum, source link) |
| PDF.js | https://github.com/mozilla/pdf.js/releases | Replace `src/PlainViewer.App/Assets/pdf/pdfjs`, keep the Liberation Sans 2.x fonts, update notices |
| Markdig, ExcelNumberFormat, WebView2 SDK | NuGet | Update the version in the project file and `.tools\feed`, update notices |
| WebView2 Runtime | Installed copies update themselves through Microsoft's update service | For each release, download the offline installer again into `.tools\webview2` (one-time setup, step 6) so new installs start from a current runtime |

## Signed release builds (SignPath)

**Status: SignPath Foundation declined the application on 2 Oct 2026** (too new; reapplying later is welcome), so the workflow builds unsigned installers. Signed installs go through the Microsoft Store instead (next section, DECISIONS.md D15). The set-up below stays for a later reapplication.

Zain chose SignPath Foundation (30 Sep 2026): free code signing for open-source projects, with a certificate issued to SignPath Foundation (so Windows shows "SignPath Foundation" as the publisher). Conditions: https://signpath.org/terms. The README's "Code signing policy" section is required by them.

**The workflow** `.github/workflows/release.yml` (run it from the repository's Actions tab: "Release build", "Run workflow") builds on a clean GitHub-hosted Windows machine. `scripts/ci-prepare.ps1` fetches the same pinned tools as the one-time setup below and checks each one (NuGet packages by SHA-256, Inno Setup by GitHub's digest and its signature, LibreOffice by The Document Foundation's SHA-256, WebView2 by Microsoft's signature). Then `package.ps1 -Stage Publish` runs the whole test gate and publishes; SignPath signs the app's own five programs (`.signpath/artifact-configurations/binaries.xml`); `package.ps1 -Stage Installer` packs the signed programs; SignPath signs the installer (`installer.xml`). The result is the workflow artifact `PlainViewer-Setup-<version>-x64` (installer and `.sha256`), ready to attach to a GitHub release. Until SignPath is set up, the same workflow builds an unsigned installer.

**Each release:** change the version, push, run the workflow, approve the two signing requests in SignPath when it asks (within three hours each, or the run stops), download the artifact, run the Windows Sandbox test on it (`sandbox-test.ps1 -Installer <file>`), then publish.

**One-time set-up by Zain** (accounts, approvals and secrets are his to handle):

1. Turn on two-factor authentication for GitHub (SignPath requires it for every team member).
2. Apply at https://signpath.org/apply for the project https://github.com/zain-ul-abdain/plain-viewer. Be ready to explain: the bundled Microsoft WebView2 Runtime installer (proprietary, from Microsoft, for PCs that lack this Windows component) and the bundled LibreOffice and .NET runtime (open source, unsigned or signed by their publishers).
3. After acceptance, in SignPath: link the predefined trusted build system "GitHub.com" to the project; create the artifact configurations `binaries` and `installer` from the two files in `.signpath/artifact-configurations`; use the signing policy SignPath Foundation sets up (usually `release-signing`) with Zain as approver; create an API token for a user with submitter rights.
4. In the GitHub repository settings: add the secret `SIGNPATH_API_TOKEN` and the variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` and `SIGNPATH_SIGNING_POLICY_SLUG`. Optionally install the SignPath GitHub App.
5. Run the workflow once and approve both requests. If SignPath rejects the installer's product name or version, that is because Inno Setup pads these fields with spaces: tell the agents, who will change the `installer` configuration.

Not signed: the uninstaller that Inno Setup writes during installation.

## Microsoft Store package (MSIX)

The Store signs MSIX packages it accepts (DECISIONS.md D15), so this route needs no certificate.

**Tools (once):** Microsoft's SDK build tools package (makeappx, signtool), approved by Zain on 2 Oct 2026: download `microsoft.windows.sdk.buildtools.<version>.nupkg` from `https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/<version>/`, check it with `dotnet nuget verify --all <file>` (Microsoft Corporation and nuget.org signatures) and unzip it to `.tools\winsdk-buildtools-<version>`. Covered by the Windows SDK licence terms.

**Each release:**

1. Publish as for the installer: `.\scripts\package.ps1 -Stage Publish` (runs the full test gate).
2. Test the package on a clean PC: `.\scripts\package-msix.ps1 -TestSign`, then `.\scripts\msix-sandbox-test.ps1`. The local test certificate is trusted inside Windows Sandbox only, never on a real PC, and is never used for distribution.
3. Build the Store upload with the identity Partner Center shows under **Product management > Product identity** (after the name is reserved): `.\scripts\package-msix.ps1 -IdentityName <Package/Identity/Name> -Publisher "<Package/Identity/Publisher>" -PublisherDisplayName "<Package/Properties/PublisherDisplayName>"`. The result, `artifacts\msix\PlainViewer-<version>.0-x64.msix`, is unsigned: upload it in the submission's **Packages** page. The version's last part must stay 0.
4. Listing text and pictures: docs/STORE-LISTING.md and docs/images/store.

**Differences from the EXE installer:** no optional firewall rules (they need an administrator prompt; About shows that they are absent), no bundled WebView2 installer (Windows 11 includes the runtime), and the converter profile in `%USERPROFILE%\AppData\LocalLow\PlainViewer` (about 1 MB) stays after the app is removed, because a package cannot run cleanup code. The Store updates installed copies itself.
## Code coverage

`scripts\coverage.ps1` runs the core, Markdown, Office safety and worker test programs and the smoke test under Microsoft's **dotnet-coverage** and writes `artifacts\coverage\summary.txt` (line coverage per part and per file) and `coverage.cobertura.xml`. Only Plain Viewer's own app, core library and worker are measured; the page scripts (JavaScript) are not. Telemetry is switched off for the runs.

Set-up (once): download `dotnet-coverage.<version>.nupkg` from `https://api.nuget.org/v3-flatcontainer/dotnet-coverage/<version>/` into `.tools\downloads`, check it with `dotnet nuget verify --all` (Microsoft Corporation and nuget.org signatures), then `dotnet tool install dotnet-coverage --version <version> --tool-path .tools\dotnet-coverage --add-source .tools\downloads`. Zain approved version 18.11.2 and accepted its Microsoft licence terms (free use for developing and testing; the tool may send usage data to Microsoft, so the script opts out) on 3 Oct 2026. The tool is never shipped with the app.
## Windows warnings

What users see while installers are unsigned:

- **SmartScreen:** "Windows protected your PC" when the installer starts. They continue with "More info" > "Run anyway". The warning fades for a signed file as it builds reputation; for unsigned files it stays.
- **Smart App Control** (Windows 11, on some clean installs): when it is on, it can block unsigned programs outright, with no "Run anyway". `PlainViewer.exe` and `PlainViewer.Worker.exe` are unsigned; the .NET runtime files are signed by Microsoft and LibreOffice's by its publisher.
- Some antivirus products scan unsigned installers more strictly.

Signed installers show "SignPath Foundation" as the verified publisher. SmartScreen can still warn until the signed files have built up reputation. A self-signed certificate may be used for local testing only, never for distribution (specification).

## Open decisions for Zain

- **Code signing.** Decided 30 Sep 2026: SignPath Foundation (free; see "Signed release builds"). Waiting for Zain's application and set-up.
- **Inno Setup commercial licence.** Inno Setup's licence allows commercial use for free, but since 2025 its authors ask commercial users with annual revenue above USD 5,000 to buy a licence (Single User, Team 2–5 users, Enterprise; one-time payment with two years of updates; price shown at checkout). They state it is not strictly required.
- **Where to publish.** GitHub Releases (this repository is private, so users could not download from it; a public repository or another host is needed), your own website, winget (needs a public download link) or the Microsoft Store (not tested with LibreOffice inside).
- **Terms page wording** (`installer/terms.txt`): a draft; review it before the beta. Plain Viewer's own licence is chosen at the public release.
- **ARM64 installer:** builds since 5 Oct 2026 (section "ARM64 installer" above) but is untested: no ARM64 PC is available. Publish it only after a test on one, or label it untested on the release page.
