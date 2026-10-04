<p align="center"><img src="docs/images/logo.svg" width="112" alt="Plain Viewer logo"></p>

<h1 align="center">Plain Viewer</h1>

<p align="center">A simple, read-only document viewer for Windows 11.<br>
Open a file, read it, close it. Offline, no Microsoft Office needed, and it never changes your files.</p>

<p align="center"><a href="https://github.com/zain-ul-abdain/plain-viewer/releases"><b>Download</b></a> ·
<a href="#how-to-install">How to install</a> · <a href="#how-to-use">How to use</a> ·
<a href="https://github.com/zain-ul-abdain/plain-viewer/issues/new/choose">Report a problem</a></p>

<table>
<tr><td><img src="docs/images/screenshot-document.png" alt="A Word newsletter shown as pages, with page thumbnails"></td><td><img src="docs/images/screenshot-spreadsheet.png" alt="An Excel budget with its cell formatting and two charts"></td></tr>
<tr><td><img src="docs/images/screenshot-presentation.png" alt="A PowerPoint presentation with its slide strip"></td><td><img src="docs/images/screenshot-markdown-dark.png" alt="A Markdown document in the dark theme"></td></tr>
</table>

**Status: public beta.** The installer is not code-signed yet, so Windows shows a warning when you install it (see below and the [code signing policy](#code-signing-policy)).

## What it opens

| Kind | File types |
|---|---|
| PDF | .pdf |
| Word and other documents | .docx .docm .dotx .dotm, .doc .dot, .odt .ott, .rtf |
| Excel and other spreadsheets | .xlsx .xlsm .xltx .xltm, .xls .xlt, .ods .ots .fods |
| PowerPoint and other presentations | .pptx .pptm .potx .potm .ppsx .ppsm, .ppt, .odp |
| Pictures | .jpg .jpeg .jfif .png .gif .bmp .ico .webp .avif .svg .tif .tiff .heic .heif |
| Web pages and books | .html .htm .xhtml, .mht .mhtml (saved web pages), .epub |
| Text and data | .txt .csv .tsv .md .markdown .json .xml .log .ini .yaml .yml |
| Code and project files (shown as text) | .c .h .cs .java .js .vb .css .php .asp .aspx .razor .config .csproj .sln |

Files with macros open with the macros removed or ignored: they never run. Web pages and books open with their scripts removed; nothing they refer to outside the file is loaded. Password-protected Word, Excel and PowerPoint files open after you type the password. What each format shows, and what it does not, is in [docs/SUPPORT.md](docs/SUPPORT.md).

## How to install

You need Windows 11 on a normal Intel or AMD PC (x64) and about 1.3 GB of free disk space. No administrator rights are needed.

1. Download `PlainViewer-Setup-<version>-x64.exe` (the newest version at the top) from the [Releases page](https://github.com/zain-ul-abdain/plain-viewer/releases).
2. Run it. Windows may show **"Windows protected your PC"** because the installer is not signed yet. Click **More info**, check the file name, then **Run anyway**.
3. Accept the terms and choose the options:
   - **Add Plain Viewer to "Open with"** (on by default): right-click a file in File Explorer and choose **Open with > Plain Viewer**. Your default apps do not change; to make Plain Viewer the default, choose it in **Settings > Apps > Default apps**.
   - **Block with Windows Firewall** (off by default): an extra safety layer that stops the parts of Plain Viewer that read documents from reaching the network. Windows asks once for administrator permission.
   - **Install the Microsoft Edge WebView2 Runtime** (only shown if your PC lacks it): needed for PDF, Office and picture files. It is included in the installer.
4. Click **Install** (one to two minutes) and start Plain Viewer from the Start menu.

**To update**, run the newer installer; it installs over the old version. **To uninstall**, open **Settings > Apps > Installed apps**, find Plain Viewer and choose **Uninstall**. Your documents are never touched.

If you can check a download: each release has a `.sha256` file; in PowerShell, `Get-FileHash PlainViewer-Setup-<version>-x64.exe` shows the same code.

## How to use

**Open a file** in any of these ways; each file opens in its own window:
- click **Open file** (or press **Ctrl+O**),
- drag a file onto the window,
- right-click a file in File Explorer and choose **Open with > Plain Viewer**,
- double-click it, if you made Plain Viewer the default app for that type.

**Read it:**
- **PDF and Word:** scroll through the pages, type a page number in the box, or click **Thumbnails** to see small pictures of the pages. **Fit width** and **Fit page** size the pages to the window.
- **PowerPoint:** one slide at a time with a strip of slides on the left; use the arrow keys or Page Up and Page Down.
- **Excel:** click the sheet tabs at the bottom, or press **Ctrl+Page Up** and **Ctrl+Page Down**. Values are shown as saved: formulas are never recalculated.
- **Pictures:** zoom, fit, and turn them with the rotate buttons (**Ctrl+R**, **Ctrl+Shift+R**). The file itself never changes.
- **Markdown:** switch between the formatted view and the **Markdown source**.
- **Text and CSV:** if characters look wrong or columns are split wrongly, choose the encoding or delimiter from the lists.

**Search:** press **Ctrl+F**, type, and press **Enter**. **F3** goes to the next match and **Shift+F3** to the previous one; the status line shows how many matches there are.

**Copy:** select text or cells and press **Ctrl+C**.

**Themes:** choose **System**, **Light** or **Dark** in the theme list; your choice, the window size and position are remembered. Windows' contrast themes are followed too.

**Links** in documents never open by themselves. Clicking a web or email link shows its full address and asks before opening it in your browser.

| Keys | Action |
|---|---|
| Ctrl+O | Open a file |
| Ctrl+F, Enter | Find |
| F3 / Shift+F3 | Next / previous match |
| Ctrl+C | Copy the selection |
| Ctrl+Plus / Ctrl+Minus / Ctrl+0 | Zoom in / out / reset |
| Page Up / Page Down, Home / End | Move through the document |
| Ctrl+Page Up / Ctrl+Page Down | Previous / next sheet |
| Ctrl+R / Ctrl+Shift+R | Rotate a picture right / left |
| F11 | Full screen |
| Ctrl+W | Close the window |

## Privacy and safety

Plain Viewer never uploads anything, has no account or sign-in, sends no usage data or crash reports, and never checks for updates. Documents are read by a separate, restricted process with time and memory limits; PDF pages, spreadsheets and pictures are drawn in a locked-down browser engine that may load nothing but the document itself; Office and OpenDocument files are converted by a bundled LibreOffice with macros and links switched off, from a private copy with every reference to outside content removed. Temporary copies are deleted when you close the document. Details and limits: [docs/SUPPORT.md](docs/SUPPORT.md).

## Code signing policy

The installers on the Releases page are not signed yet. SignPath Foundation, which signs open-source projects for free, declined the project in October 2026 because it is still new; it may apply again once more people use it. A signed version is planned through the Microsoft Store, where Microsoft signs the package. Every installer is built from this repository by the [release workflow](.github/workflows/release.yml) on GitHub's own build machines, after the full test suite passes there.

- Committers and reviewers: [Zain ul abdain](https://github.com/zain-ul-abdain) (repository owner)
- Approvers: [Zain ul abdain](https://github.com/zain-ul-abdain)

Privacy policy: this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. The installer may add the Microsoft Edge WebView2 Runtime, a Windows component that Microsoft updates under [Microsoft's privacy statement](https://privacy.microsoft.com/privacystatement); Plain Viewer itself never uses the network.

## Build, test and package

Windows with the .NET 10 SDK (installed normally or under `.tools/dotnet`). From the repository root in PowerShell:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1               # core tests, including every fixture in tests/corpus/manifest.json
.\scripts\test-markdown.ps1
.\scripts\test-office-safety.ps1
.\scripts\test-worker.ps1
.\scripts\test-app.ps1
.\scripts\coverage.ps1        # optional: line coverage report (needs dotnet-coverage, see docs/RELEASING.md)
.\scripts\smoke-test.ps1         # opens files of every format in real windows
.\scripts\security-smoke.ps1     # hostile and broken files, with a listener that records any network request
.\scripts\run.ps1 -File "$PWD\tests\corpus\complex.markdown"
```

Office formats need LibreOffice for development: `.\scripts\fetch-libreoffice.ps1`. The offline installer, its one-time setup and the release steps are in [docs/RELEASING.md](docs/RELEASING.md). Test fixtures are generated by `tests/corpus/generate` (see [tests/corpus/SOURCES.md](tests/corpus/SOURCES.md)). The logo is [docs/images/logo.svg](docs/images/logo.svg); `scripts/make-icon.ps1` makes the app icon from it.

## Project documents

- [docs/SPECIFICATION.md](docs/SPECIFICATION.md): the full requirements.
- [docs/DECISIONS.md](docs/DECISIONS.md): technology decisions and their evidence.
- [docs/RELEASE-NOTES.md](docs/RELEASE-NOTES.md): what changed in each version.
- [docs/STORE-LISTING.md](docs/STORE-LISTING.md): draft Microsoft Store listing (not submitted).
- [AGENTS.md](AGENTS.md): shared working instructions for the agents that worked on this project; [docs/TASKS.md](docs/TASKS.md) and [docs/HANDOFF.md](docs/HANDOFF.md): task records and handoff notes.

## Licence

Plain Viewer is released under the MIT licence ([LICENSE](LICENSE)). The components it bundles keep their own licences: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
