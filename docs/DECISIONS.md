# Technology decisions

Status: initial draft by Codex, 27 September 2026. Facts verified against official sources by Agent 2 on 27 September 2026 (source list at the end, each with the date checked). No commercial trial, key or licence was obtained. Nothing below is a measurement unless it says "measured".

## Selected direction

Use .NET 10 and WPF for the Windows shell, with a core library independent of UI. WPF provides native keyboard, text selection, accessibility, and virtualized controls, and it builds with the .NET SDK alone, without Visual Studio. Use its Fluent theme. Build a small development preview first; the production release requires the full acceptance gates in SPECIFICATION.md.

Use a hybrid renderer: Markdig for Markdown syntax mapped to safe native display elements; a dedicated streaming spreadsheet reader and grid; PDF.js in a tightly restricted local WebView2 surface for PDFs; LibreOffice conversion for DOCX/PPTX only after worker isolation and disabled external-content behavior are verified. The PDF/Office paths are recommendations, not currently enabled integrations. Do not distribute an insecure conversion shortcut.

## Decision register

| ID | Decision | Status | Main reason |
|---|---|---|---|
| D1 | .NET 10 + WPF shell with the Fluent theme | Accepted | Builds with the SDK alone; mature UI Automation. Microsoft says Fluent support "is still in progress" in .NET 10 [S1], so high-contrast and per-control styling must be tested, not assumed |
| D2 | Hybrid rendering, one engine per format family (table below) | Accepted | No single free engine meets fidelity, grid, search and safety needs together |
| D3 | LibreOffice, headless, converts DOCX/PPTX (and optional DOC/PPT/RTF/ODT/ODP) to PDF inside the isolated worker | Accepted, gated on the isolation tests in "Validation gates" | Only free engine with broad Word/PowerPoint layout support, including old binary formats. MPL-2.0 does not require publishing this app's source [S4] |
| D4 | PDF.js (Apache-2.0) in WebView2 displays PDFs and LibreOffice output | Accepted | Gives text layer, search and selection. Windows.Data.Pdf only renders page images; `PdfPage` has no text API [S9] |
| D5 | Spreadsheets: ExcelDataReader (MIT) + ExcelNumberFormat (MIT) feeding a virtualized WPF grid | Proposed | Forward-only, row-by-row reader; exposes merged cells, column widths and number-format strings; covers .xlsx and old .xls [S10, S11]. Formula evaluation is not part of its API; cached-value behaviour must be proven by fixtures |
| D6 | Markdown: Markdig (BSD-2-Clause, CommonMark 0.31.2, pipe tables, task lists) rendered to native WPF elements; HTML nodes stay in the AST and are shown as literal text | Accepted (revised after Codex review) | CommonMark-compliant parser [S12]. Keeping HTML nodes as literal text is safe because nothing instantiates HTML or XAML, and it preserves layout better than switching HTML parsing off. Compare fidelity before changing it |
| D7 | Installer: Inno Setup, per-user, EXE | Implemented 27 Sep 2026 with Inno Setup 7.1.0 (Zain approved the download): `installer/PlainViewer.iss`, `scripts/package.ps1`, see RELEASING.md | EXE rather than MSIX: LibreOffice inside an MSIX container is untested, and an EXE installs per user without administrator rights. The licence allows commercial use without a fee [S7], but since 2025 the authors ask commercial users with annual revenue above USD 5,000 to buy a commercial licence, "not strictly required" [S7b]; Zain decides. WiX v6+ requires organisations with more than USD 10,000 annual revenue to pay an Open Source Maintenance Fee [S8] |
| D8 | Bundle OFL-1.1 fallback fonts for LibreOffice: Carlito (Calibri metrics), Caladea (Cambria), Liberation Sans/Serif/Mono (Arial, Times New Roman, Courier New) | Proposed | Metric-compatible substitutes keep line breaks and page counts closer [S13–S15] |
| D9 | WebView2 Evergreen runtime, not the Fixed Version; the installer bundles Microsoft's offline Evergreen Standalone Installer and runs it when the runtime is missing | Accepted (Zain, 28 Sep 2026: bundle) | How the distribution terms are met: the app adds significant functionality; SmartScreen is off in the app (`IsReputationCheckingRequired = false`), so the SmartScreen notice of section 9(a) is not needed, and 9(b) concerns only Windows 7/8.1; users accept the installer's terms page (`installer/terms.txt`), which makes installing the runtime subject to Microsoft's own terms; the file comes straight from Microsoft and `package.ps1` refuses it unless validly signed by Microsoft Corporation; no Microsoft trademarks suggest endorsement. The indemnity for Microsoft is Zain's obligation as distributor. The runtime is a shared Windows component: uninstalling Plain Viewer leaves it. History: Fixed Version adds "over 250 MB" [S2]. Windows 11 ships the Evergreen runtime, but the installer must still check for it. If it is missing, an online bootstrapper cannot meet the offline-first-launch requirement (Codex review). Options: bundle Microsoft's offline Evergreen Standalone Installer, or state the runtime as an explicit prerequisite. Zain decides before release. Evidence (28 Sep 2026): a clean Windows 11 in Windows Sandbox has no WebView2 Runtime, so the runtime cannot be assumed; without it the app now refuses PDF/Office files with a clear message and still opens text, CSV and Markdown (TEST-RESULTS.md). Zain accepted Microsoft's WebView2 Runtime licence terms for testing, then chose to bundle (28 Sep 2026). Bundling adds about 210 MB to the installer (the x64 offline installer, 28 Sep 2026). The wording of `installer/terms.txt` is a draft for Zain to review; Plain Viewer's own licence is chosen at the public release |
| D10 | No commercial SDK in v1 | Accepted unless Zain decides otherwise | Costs and contract terms below; the free stack covers the required formats if the gates pass |
| D11 | HEIC/HEIF photos: decoded by Windows' own HEIF codec (WIC, through WPF) inside the low-integrity worker, which writes a PNG for the picture view; no decoder bundled | Implemented 29 Sep 2026 (0.4.0) | WebView2 cannot decode HEIC. Bundling a decoder (for example libheif with libde265) would add LGPL code and the HEVC patent-licensing question to the installer; Windows' codec avoids both, and the user gets it from Microsoft ("HEIF Image Extensions" and "HEVC Video Extensions", Microsoft Store; the HEVC one may cost a small fee). Without it the photo is refused with a message naming both. The codec parses the untrusted file only inside the worker (low integrity, Job Object memory and time limits); only the Windows HEIF decoder is accepted (checked by its container format), and the app re-checks the PNG before showing it |
| D12 | Excel pictures and charts: pictures stored in the workbook are identified by their bytes and written by the worker to the work folder; charts are drawn as SVG in the grid page from the values cached in the chart part | Implemented 29 Sep 2026 (0.4.0) | Keeps "saved values only": chart data is never recalculated from cells. Pictures are served to the page from the document host only under names the worker may use (`media-<sheet>-<n>.<type>`), re-identified by the app before serving; the page's CSP allows images from that host only. Linked pictures (`r:link`) are counted and never resolved |
| D13 | Windows 10 support: postponed; installers stay Windows 11 only | Postponed (Zain, 30 Sep 2026: "skip Windows 10 for now") | Zain asked for Windows 10 support. A clean Windows 10 22H2 test machine (scripts/win10-vm-test.ps1: Microsoft's image, Hyper-V, no network) showed that the installer, firewall rules, "Open with", WebView2 and uninstall work, but the app does not start: .NET 10 stops it with "Your Windows doesn't fully support CET. Please install all available Windows updates." .NET 9+ marks apps compatible with CET (hardware shadow stacks, a protection against return-oriented exploits), and that Windows 10 build lacks the support. Zain chose to keep CET (not `<CETCompat>false</CETCompat>`) and require an updated Windows 10. Verifying with the October 2025 cumulative update (KB5066791, downloaded and checked, SHA-1 3210d264...44c8) could not run because the PC lacked free memory for the virtual machine. To resume: run `win10-vm-test.ps1 -Update <that .msu>`; if it passes, allow build 19045 with a minimum update level in the installer and document it |
| D14 | The text encoding and CSV delimiter lists are hidden where they do not apply, instead of shown disabled | Accepted (Zain, 30 Sep 2026) | The specification says to disable unavailable controls with an accessible explanation. Zain chose to hide these two: shown disabled as "Auto" beside PDF, Office, picture and spreadsheet views they looked broken, and they never apply there. The encoding list shows for text, CSV, Markdown and data files, the delimiter list for CSV only; neither shows before a file is open. Other unavailable controls (for example search for pictures) stay disabled with their explanation |
| D15 | Signed installs through the Microsoft Store as an MSIX package; the GitHub installer stays unsigned | Accepted (Zain, 2 Oct 2026) | SignPath Foundation declined the application on 2 Oct 2026 (the project is too new: they look for stars, forks, articles and discussions; reapplying later is welcome). Buying a certificate needs Zain's approval and the specification forbids it for now. The Microsoft Store signs MSIX packages it accepts, so `scripts/package-msix.ps1` packs the published app and LibreOffice (manifest `installer/msix/AppxManifest.xml`; tools: Microsoft's SDK build tools package, approved and signature-checked). Trial in Windows Sandbox (`scripts/msix-sandbox-test.ps1`, local test certificate trusted in the sandbox only): every view works with package identity, including LibreOffice from the read-only package folder and the low-integrity worker; "Open with" is registered and removed with the package. Differences from the EXE installer: no optional firewall rules (they need an administrator prompt), no bundled WebView2 installer (Windows 11 includes the runtime), and the LibreOffice profile in `%USERPROFILE%\AppData\LocalLow\PlainViewer` (about 0.6 MB, not redirected by MSIX) is left after removal because a package cannot run cleanup code. Package 313 MB (EXE installer 422 MB). The GitHub installer stays unsigned until SignPath accepts a reapplication or Zain chooses a paid option |

## Engine per format

| Format | Engine | Search and copy | Status |
|---|---|---|---|
| PDF | PDF.js in WebView2 | Yes, through the PDF.js text layer | Not implemented |
| DOCX, PPTX | LibreOffice → PDF → PDF.js | Yes, if the converted PDF has text | Not implemented; gated |
| XLSX, XLS | ExcelDataReader → virtualized grid | Cell text copy; search in the core library | Not implemented |
| CSV, TXT | Core streaming parsers → grid or text view | Yes | Codex implementing |
| Markdown | Markdig AST → WPF FlowDocument | Yes, native text | Codex implementing |
| RTF, ODT, ODP, DOC, PPT (optional) | LibreOffice → PDF | As PDF | Optional; decided by fixtures |
| ODS (optional) | LibreOffice → XLSX, recalculation off → grid | As grid | Optional |

## Options compared

| Approach | Fidelity and old formats | Grid, text and accessibility | Distribution, cost and maintenance | Size, speed and exposure |
|---|---|---|---|---|
| LibreOffice plus PDF viewer | Broad import coverage; Word/slide layout needs independent Office comparisons. Old DOC/XLS/PPT require corpus validation. | PDF text layer enables search and copy; conversion loses spreadsheet grid and tabs, so spreadsheets use D5. Accessibility depends on output tagging and viewer. | MPL-2.0 (with LGPLv3+ and Apache-2.0 parts). Distributing the binaries requires telling users how to get LibreOffice's source; a "Larger Work" may use its own terms [S4]. Current releases: 26.8.0 (Fresh), 26.2.6 (Still), with x86-64 and ARM64 Windows builds [S5]. | Largest bundle; download size not published on the download page, measure it. Cold conversion overhead. Large native parser surface; must run isolated and offline. |
| Syncfusion | Document SDKs cover multiple Office formats; legacy support per product must be checked. | Separate spreadsheet/viewer components may be required. | Community Licence only for organisations under USD 1 million revenue, 5 or fewer developers, 10 or fewer employees, never more than USD 3 million outside capital, and not government-related [S16]. Accepting it is a contract, so it needs Zain's approval. | Size and cold start unknown without an approved evaluation. Proprietary parsing still needs isolation. |
| Aspose | Words, Cells and Slides are separate products. | Processing APIs alone do not provide the interactive grid/UI. | Aspose.Words for .NET alone: USD 1,199 per developer for one deployment location, USD 3,597 per developer for unlimited locations (OEM) [S17]. Cells and Slides are priced separately. | Size/performance unmeasured. Multiple SDKs and parser surfaces. |
| Apryse | PDF and Office conversion depend on selected modules. | Viewer features may reduce custom PDF work; grid still needed. | Quote-based modular pricing [S18]. | Bundle/performance unknown. |
| Separate open-source libraries | Markdig covers Markdown; Open XML parsing reads structure, not faithful Word/slide pagination. PDF.js renders PDFs. Old Office binaries need another engine. | Best control over cached spreadsheet values and virtualized grid. | Audit each pinned package and transitive licence. No per-seat charge. | Smaller components, higher implementation cost. |

## Verified configuration details

### LibreOffice profile (private per conversion, seeded before first start)

Keys and values checked in LibreOffice's own configuration schema [S6]:

| Setting | Path | Value to set | Default |
|---|---|---|---|
| Macro security level | `/org.openoffice.Office.Common/Security/Scripting/MacroSecurityLevel` | `3` (Very High) | `2` |
| Disable all macro execution (Basic, BeanShell, JavaScript, Python) | `/org.openoffice.Office.Common/Security/Scripting/DisableMacrosExecution` | `true` | `false` |
| Block links from documents outside trusted locations | `/org.openoffice.Office.Common/Security/Scripting/BlockUntrustedRefererLinks` | `true` | `false` |
| Writer: update links on load | `/org.openoffice.Office.Writer/Content/Update/Link` | **`2` = never** (0 always, 1 on request) | `1` |
| Calc: update links on load | `/org.openoffice.Office.Calc/Content/Update/Link` | **`1` = never** (0 always, 2 on request) | `2` |
| Calc: recalculate OOXML on load | `/org.openoffice.Office.Calc/Formula/Load/OOXMLRecalcMode` | `1` = never | `1` |
| Calc: recalculate ODF on load | `/org.openoffice.Office.Calc/Formula/Load/ODFRecalcMode` | `1` = never | `1` |
| Lock file beside the document | `/org.openoffice.Office.Common/Misc/UseDocumentOOoLockFile` (corrected 27 Sep 2026; was listed under `Load`) | `false` | `true` |
| System file locking | `/org.openoffice.Office.Common/Misc/UseDocumentSystemFileLocking` (corrected, as above) | `false` | `true` |
| Automatic update check | `/org.openoffice.Office.Jobs/Jobs/org.openoffice.Office.Jobs:Job['UpdateCheck']/Arguments/AutoCheckEnabled` with `oor:type="xs:boolean"` | `false` | `true` |
| Font replacement for fonts missing on the PC | `/org.openoffice.Office.Common/Font/Substitution/Replacement` and `.../FontPairs` (Always = true) | only for missing fonts: Simplified Arabic → Arial, Traditional Arabic → Times New Roman, SimHei/DengXian → Microsoft YaHei and a few others (`OfficeConverter.DefaultReplacements`) | off |

**Trap:** "never" is `2` for Writer but `1` for Calc. Copying one value to both leaves Calc on "update on request".

**Found by the Office comparison tests (27 Sep 2026), now fixed and checked in the file LibreOffice saves after a run:**
- The update-check entry was written without `oor:type`. LibreOffice rejected it and ignored every entry after it, so the update check stayed on and the dead-proxy and font settings never took effect. It is now written with its type, and last. In headless use the update check runs only when a window opens, and the network tests recorded 0 requests throughout.
- The two file-locking settings used a non-existent `Load` path (above).
- Rewriting the whole settings file before each run also removed LibreOffice's record that set-up and its extension check were done (`/org.openoffice.Setup/Office/ooSetupInstCompleted`, `LastCompatibilityCheckID`), so every run repeated the check and restarted LibreOffice. Once the profile is ready, the app now writes both back.
- Writer resets `/org.openoffice.Office.Writer/Content/Update/Field` to `true` itself. This is not relied on: the worker blanks every field code that can fetch content before LibreOffice sees the file.
- The dead-proxy values sit in a group that Windows' own proxy settings also feed (`WinInetBackend`); LibreOffice keeps ours, but the layer is treated as an extra, not a boundary.

Command line [S3]: `soffice --headless --norestore --nologo --nodefault --nolockcheck -env:UserInstallation=file:///<private profile> --convert-to pdf --outdir <private dir> <private copy>`. The input is always a private snapshot, so any lock file lands in private storage even if a setting is missed.

No schema key was found that blocks remote graphics outright. Preferences are therefore not the network boundary: the worker's containment must block network access (see gates), and the remote-image, remote-template and network-share fixtures must prove it.

### WebView2 lockdown

- Register `AddWebResourceRequestedFilter("*", All, <all source kinds>)` using the three-argument overload; the two-argument overload is deprecated and "does not behave as expected for iframes" [S19]. Deny every request that is not the app's own virtual host. Service and shared workers raise the event environment-wide.
- Also cancel `NavigationStarting` for anything but the app page, handle `NewWindowRequested` and `DownloadStarting` by cancelling, and disable developer tools in release builds.
- Serve PDF.js assets through `SetVirtualHostNameToFolderMapping`, and give PDF bytes to the page directly; never give the page a file path.
- PDF.js: keep `enableXfa` false (its default) [S20]. Set `standardFontDataUrl` and `cMapUrl` to local app assets so no font data is fetched remotely. Confirm the scripting and eval options in the pinned PDF.js version before release.

### Fonts

- Office's current default font, Aptos, is a Microsoft 365 cloud font downloaded on demand; it is not part of Windows [S21]. Documents that use it render with a substitute in any Office-free viewer. No metric-compatible free substitute is known, so this is a documented limitation.
- Carlito is "metric-compatible with Calibri" (OFL-1.1) [S13]. Liberation fonts cover Arial, Times New Roman and Courier New (OFL-1.1) [S14]. Caladea (OFL-1.1) is based on Cambo with new metrics [S15]; the Cambria match must be checked with fixtures, not assumed.

### Isolation mechanisms available

- **Job objects:** a per-process committed-memory limit (`JOB_OBJECT_LIMIT_PROCESS_MEMORY`), a job-wide limit, and kill-on-job-close [S22]. Kill-on-close ensures LibreOffice child processes die with the worker.
- **AppContainer:** restricts files, registry, network and credentials; network access must be granted explicitly, and credentials cannot be used to reach other resources [S23]. This is the only listed mechanism that blocks network and network-share access at the OS level. Whether LibreOffice runs inside an AppContainer is unknown and is the first gate below.

## Validation gates (release blockers)

1. **LibreOffice in AppContainer.** Run a conversion inside an AppContainer with no network capability. If it works, that is the network boundary. If it does not, a job object, the hardened profile and a low-integrity token are **not** an equivalent boundary (Codex review): the no-network requirement must not be weakened silently. Record the gap and get Zain's written decision before any release that enables LibreOffice.
   - **Result, 27 Sep 2026: failed.** With read/execute access granted to its folder, LibreOffice 26.2.6 started inside an AppContainer with no capabilities, then stopped with its own dialog "The application cannot be started. An internal error occurred." Even `--version` hung the same way. LibreOffice's source shows why: `RequestHandler::Enable(true)` always creates its single-instance named pipe in the global namespace, with no runtime switch on Windows (`desktop/source/app/app.cxx`, `officeipcthread.cxx`, branch libreoffice-26-2), and pipe errors are reported as this generic start-up error. AppContainer processes cannot create such pipes. Making it work would mean patching and maintaining our own LibreOffice build. The test container profile and its folder permission were removed afterwards.
   - **What the development preview does instead** (layered, not an OS network boundary): the worker refuses encrypted, macro-carrying, DTD-bearing and oversized packages and writes a private copy with every outside reference removed except hyperlinks, and with content-fetching field codes blanked; LibreOffice runs on that copy from a profile whose hardened settings are rewritten every run, with a dead local proxy for any web request, inside a Job Object with memory, process and UI limits. Tests: before any sanitising, LibreOffice itself made 0 requests for the remote-image, network-share, remote-template and INCLUDEPICTURE fixtures; with the full pipeline, `scripts/security-smoke.ps1` records 0 requests across 32 hostile or broken files.
   - **Options put to Zain:** (a) accept the layered protection above and document the limit; (b) have the installer add a Windows Firewall rule blocking the bundled LibreOffice from the network, which gives an OS boundary but needs administrator rights at install time, against the per-user preference; (c) fund a patched LibreOffice build; (d) evaluate a commercial SDK (paid, needs approval).
   - **Decision, 27 Sep 2026 (Zain): low integrity + firewall.** LibreOffice (and, for consistency, the parsing worker) runs at low integrity inside its job object, so it cannot write the user's files or tamper with other programs; the installer offers an optional one-time administrator prompt that adds Windows Firewall rules blocking these programs from the network. Documented limits: without the firewall rules there is no OS-level network block; with them, network access that Windows performs on the program's behalf through system services (DNS lookups, network-share access by the SMB client) is not covered, and low integrity does not stop reading the user's files. This is weaker than an AppContainer and is described that way in SUPPORT.md.
2. **Hostile fixtures.** Remote image, remote template, external workbook link, UNC and WebDAV paths, XML external entity, ZIP bomb: zero network requests and no files beside the source.
3. **Fluent theme.** Check high contrast, 100–300% scaling and every control used.
4. **Spreadsheet values.** Formulas with and without cached results show the cached value or "Result unavailable"; nothing recalculates.
5. **Sizes and timings.** Measured 27 Sep 2026 (x64, version 0.1.0): LibreOffice 1,557 MB unpacked, 722 MB after `trim-libreoffice.ps1`; app with .NET included 142 MB; installer 200 MB; installed 869 MB; silent install 40 s including the converter pre-warm (8 s on a fresh profile). Cold/warm first-page times on reference hardware still pending (`scripts/measure.ps1`).
6. **ARM64.** LibreOffice publishes an ARM64 Windows build [S5] and .NET supports ARM64. Since 5 Oct 2026 a native ARM64 installer builds (`package.ps1 -Arch arm64`, docs/RELEASING.md) but is untested on an ARM64 PC; the x64 installer installs on ARM64 PCs (Inno Setup `x64compatible`) and would run under Windows' x64 emulation, also untested.

## Pitfalls (from the first draft, still apply)

- Office preview handlers are not a reliable Office-free dependency. Do not depend on installed Office or shell preview handlers.
- Markdown must create only trusted native UI objects from syntax nodes, never instantiate XAML/HTML from the file. Image references never cause file or network reads. Unsafe link schemes are inert text. HTML is displayed literally.
- Only register acceptance-tested formats and never overwrite defaults.
- No production signing certificate will be bought. Unsigned preview builds are allowed; distribution signing is a later documented step.

## Sources (all checked 27 September 2026)

- [S1] What's new in WPF for .NET 10 (page dated 10 Feb 2026): https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100
- [S2] Distribute your app and the WebView2 Runtime: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution
- [S3] LibreOffice command-line parameters: https://help.libreoffice.org/latest/en-US/text/shared/guide/start_parameters.html
- [S4] LibreOffice licences: https://www.libreoffice.org/about-us/licenses/
- [S5] LibreOffice download page (26.8.0 and 26.2.6; x86-64 and aarch64 MSI): https://www.libreoffice.org/download/download-libreoffice/
- [S6] LibreOffice configuration schema: https://raw.githubusercontent.com/LibreOffice/core/master/officecfg/registry/schema/org/openoffice/Office/Common.xcs, `.../Writer.xcs`, `.../Calc.xcs`
- [S7] Inno Setup licence: https://jrsoftware.org/files/is/license.txt
- [S7b] Inno Setup commercial licences (request to commercial users, "not strictly required"): https://jrsoftware.org/isorder.php
- [S8] WiX Open Source Maintenance Fee: https://docs.firegiant.com/wix/osmf/ and https://github.com/wixtoolset/issues/issues/8974
- [S9] Windows.Data.Pdf.PdfPage members: https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfpage
- [S10] ExcelDataReader: https://github.com/ExcelDataReader/ExcelDataReader
- [S11] ExcelNumberFormat: https://github.com/andersnm/ExcelNumberFormat
- [S12] Markdig: https://github.com/xoofx/markdig
- [S13] Carlito: https://github.com/googlefonts/carlito
- [S14] Liberation fonts: https://github.com/liberationfonts/liberation-fonts
- [S15] Caladea: https://github.com/huertatipografica/Caladea
- [S16] Syncfusion Community Licence: https://www.syncfusion.com/products/communitylicense
- [S17] Aspose.Words for .NET pricing: https://purchase.aspose.com/pricing/words/net
- [S18] Apryse pricing: https://apryse.com/en-au/pricing
- [S19] CoreWebView2.AddWebResourceRequestedFilter: https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.addwebresourcerequestedfilter
- [S20] PDF.js API, getDocument parameters: https://mozilla.github.io/pdf.js/api/draft/module-pdfjsLib.html
- [S21] Cloud fonts in Office: https://support.microsoft.com/en-us/office/cloud-fonts-in-office-f7b009fe-037f-45ed-a556-b5fe6ede6adb
- [S22] JOBOBJECT_EXTENDED_LIMIT_INFORMATION: https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information
- [S23] AppContainer isolation: https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation

Estimates and unverified items are labelled as such. Independent Office files, cold/warm timing measurements, native containment, renderer configuration, dependency redistribution audit and installer tests remain release blockers.
