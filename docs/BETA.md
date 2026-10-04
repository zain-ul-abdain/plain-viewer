# Plain Viewer beta: a guide for testers

Thank you for trying Plain Viewer. This is an early test version (0.5.0). It will have rough edges, and your reports help us find them.

## What Plain Viewer is

Plain Viewer opens documents so you can read them. It does nothing else.

- **Read-only.** It never changes, renames, moves or deletes your files, and it never saves anything next to them. You cannot edit a document in it.
- **Offline.** It never uses the internet. It does not upload your documents, send usage data or crash reports, or check for updates. It works the same with Wi-Fi switched off.
- **No Microsoft Office needed.** It opens Word, Excel and PowerPoint files on its own.

It opens these files:

- **Documents:** PDF (.pdf); Word (.docx, .doc, and templates and macro files .dotx .docm .dotm); PowerPoint (.pptx, .ppt, and shows, templates and macro files .ppsx .potx .pptm .potm .ppsm); Excel (.xlsx, .xls, and .xlsm .xltx .xltm); OpenDocument (.odt .ods .odp, templates .ott .ots, and flat .fods); Rich Text (.rtf); Word and Excel 97-2003 templates (.dot .xlt). Password-protected Word, Excel and PowerPoint files open after you type the password.
- **Pictures:** JPEG (.jpg .jpeg .jfif), PNG, GIF, BMP, icons (.ico), WebP, AVIF, SVG, TIFF (.tif .tiff, including multi-page scans) and iPhone photos (.heic .heif).
- **Web pages and books:** web pages (.html .htm .xhtml), saved web pages (.mht .mhtml) and EPUB books. Scripts are removed and never run; pictures and styles stored outside the file are not loaded.
- **Text and data:** text (.txt), CSV and tab-separated files (.tsv), Markdown (.md .markdown), JSON, XML, log files (.log), settings files (.ini), YAML (.yaml .yml), and source code and project files (.c .h .cs .java .js .vb .css .php .asp .aspx .razor .config .csproj .sln), shown as text.

Files with macros open with the macros removed or ignored: they never run, and the status line says so. Very old Word 6.0/95 and Excel 5.0/95 files are not supported; Plain Viewer tells you so instead of opening them.

## What you need

- Windows 11 on a normal Intel or AMD PC (x64). Windows 10 is not supported yet. PCs with ARM processors (for example some Surface and Snapdragon laptops) have not been tested.
- About 1.3 GB of free disk space. Plain Viewer itself takes about 870 MB; the rest is for a Windows component it may install (see "WebView2" below) and for temporary copies while you view documents.
- No administrator rights, unless you choose the optional firewall rules.

## Installing

1. Run `PlainViewer-Setup-0.5.0-x64.exe` (about 400 MB).
2. **"Windows protected your PC".** Windows shows this blue warning because the installer is not yet signed with a publisher certificate. It does not mean a virus was found. Click **More info**, check that the file name is `PlainViewer-Setup-0.3.0-x64.exe`, then click **Run anyway**.
3. **Smart App Control.** On some newly set-up Windows 11 PCs, Smart App Control is switched on. It may block the installer or Plain Viewer completely, with no "Run anyway" button. If that happens, please tell us rather than switching Smart App Control off just for this test (on many Windows versions it cannot be switched back on without resetting the PC).
4. Read and accept the terms page.
5. Choose the options (next section) and click **Install**. Installing takes one to two minutes. At the end it prepares the part that shows Word and PowerPoint files, so the first one you open is quick.

If you already have an earlier beta, just run the new installer: it updates Plain Viewer in place, and nothing needs uninstalling first.

Plain Viewer installs for your Windows account only, in `%LOCALAPPDATA%\Programs\Plain Viewer`, and adds a Start menu entry.

If you were given a checksum (a long code in a `.sha256` file), you can check the download in PowerShell with `Get-FileHash PlainViewer-Setup-0.5.0-x64.exe`; the code shown should match.

## The installer's options

- **Add Plain Viewer to "Open with"** (on by default). Right-click a supported file in File Explorer and choose **Open with** > **Plain Viewer**. Your default apps do not change. If you want double-clicking to use Plain Viewer, choose it yourself in Windows Settings > Apps > Default apps.
- **Block with Windows Firewall** (off by default). Adds firewall rules that stop the parts of Plain Viewer that read documents from reaching the network, as an extra safety layer. Windows asks once for administrator permission. The About button in Plain Viewer shows whether the rules are on. Uninstalling removes them.
- **Install the Microsoft Edge WebView2 Runtime** (only shown if your PC lacks it; on by default). PDF, Word, Excel, PowerPoint and picture files need this Windows component; most Windows 11 PCs already have it. It is Microsoft software under Microsoft's licence and stays installed if you remove Plain Viewer. Without it, Plain Viewer still opens text, CSV, Markdown and data files, and explains what is missing for the others.
- **Create a desktop shortcut** (off by default).

## What to try

Please use your own everyday documents as well as any test files. Things worth trying:

- **Each file type:** PDF, Word, Excel, PowerPoint (new and old formats), OpenDocument, RTF, pictures (including TIFF scans), CSV, text, Markdown and data files. Do they look like they do in the program that made them? Are tables, pictures, headers and footers in the right place? In Excel, do colours, fonts, borders and column widths match?
- **Pictures:** zoom, fit, and the rotate buttons (Ctrl+R turns right, Ctrl+Shift+R left; the file itself never changes). Phone photos should appear the right way up.
- **Page thumbnails:** the Thumbnails button beside the page number, for PDF and Word documents.
- **Opening files in different ways:** the Open button (or Ctrl+O), dragging a file onto the window, right-click > Open with, and double-click if you made Plain Viewer your default. Each file opens in its own window.
- **Search:** Ctrl+F, type a word, press Enter. F3 goes to the next match, Shift+F3 to the previous one.
- **Zoom:** Ctrl+Plus, Ctrl+Minus, Ctrl+0 to reset. For PDF, Word and PowerPoint also try fit to page and fit to width.
- **Moving around:** Page Up, Page Down, Home, End; the page or slide number box; in Excel, the sheet tabs and Ctrl+Page Up / Ctrl+Page Down.
- **Copying:** select text or cells and press Ctrl+C, then paste somewhere else.
- **Markdown:** switch between the rendered view and "Markdown source".
- **Text and CSV:** if characters look wrong, or columns are split in the wrong place, try the encoding and delimiter choices.
- **Themes:** the theme box (System, Light, Dark; your choice is remembered), and Windows' own dark mode and contrast themes (Settings > Accessibility > Contrast themes). PDF, Word, Excel and PowerPoint keep their own colours; the rest follows the theme.
- **Keyboard only:** can you do everything with Tab, the arrow keys, Enter and the shortcuts above? F11 switches full screen on and off; Ctrl+W closes the window.
- **Screen scaling:** if you use 150%, 200% or more (Settings > System > Display > Scale), or move the window between two monitors, does everything stay sharp and readable?
- **Narrator** or another screen reader, if you use one.
- **Big files:** a long PDF (hundreds of pages), a large Excel file, a CSV of tens or hundreds of megabytes. The window should stay responsive and show progress; large searches can be cancelled.
- **Awkward files:** a password-protected file, a damaged or half-downloaded file, an empty file, a file with the wrong extension, a file on OneDrive that is not downloaded. Plain Viewer should explain what happened in plain words rather than crash or show a blank window.

## Known limitations in this version

- **Word and PowerPoint** are shown by converting them to pages with LibreOffice, so they are close to, but not always exactly like, Microsoft Office: long Word documents can shift by a page, and fonts your PC does not have are replaced with the closest Windows font. Office's newer default font, Aptos, is not part of Windows. PowerPoint shows static slides: animations, transitions, sound and video do not play.
- **Excel** shows the values saved in the file; it never recalculates formulas. If a formula has no saved result you see "Result unavailable" (open and save the file in a spreadsheet program to fix it). Cell colours, fonts, borders and alignment are shown (also in .xls and .ods), and so are pictures and charts. Conditional formatting (colour scales, data bars, icons and highlight rules) is worked out from the saved values, in .xlsx and .ods files and, for the simpler rules older Excel versions have, in .xls files; rules written as formulas are not shown, and a note says how many. Row heights are kept, so wrapped text shows as many lines as its row holds (except in sheets with more than 10,000 rows). Hidden sheets stay hidden.
- **Pictures** (and TIFF scans) have no text, so search is switched off for them. iPhone (HEIC) photos need Microsoft's "HEIF Image Extensions" and "HEVC Video Extensions" from the Microsoft Store (many PCs have them already); without them Plain Viewer says so.
- **Excel charts** are drawn from the values saved with them. Common kinds (column, bar, line, area, pie, doughnut, scatter) are shown; others show a note. Shapes and text boxes are shown, grouped ones too (common outlines, colours and their text), in .xlsx, .xls and .ods files; SmartArt is not. Older (.xls) and OpenDocument (.ods) spreadsheets show their pictures and charts too.
- **Markdown:** pictures are shown as their description, never loaded. Embedded HTML is shown as text. Math and diagrams are shown as code.
- **Large text files** (over 4 MB) are shown as numbered lines without word wrap, and copying works on whole lines.
- **CSV** files can have up to 16,384 columns (as in Excel) and cannot be sorted or filtered.
- **Data files** (JSON, XML, YAML and so on) are shown as plain text, without colours or folding.
- **Links** are never opened automatically. Clicking a web or email link shows the full address and asks first; other kinds of links are shown as text only.
- **Not tested yet:** Narrator, display scaling above 100%, and ARM64 PCs. The title bar says "Development preview".

## Uninstalling

Open Windows Settings > Apps > Installed apps, find **Plain Viewer**, click the three dots and choose **Uninstall**. This removes the program, its "Open with" entries, its firewall rules if you added them, and its private working folders. It never touches your documents. The Microsoft Edge WebView2 Runtime stays, because other programs share it.

## Your privacy: what stays on your PC

Nothing leaves your PC. Plain Viewer has no account, no sign-in, no usage statistics, no crash reporting and no update check.

While a document is open, Plain Viewer may keep a private temporary copy of it in `%USERPROFILE%\AppData\LocalLow\PlainViewer`. It deletes the copy when you close the document, and cleans up leftovers the next time it starts if it ever crashed. It does not keep a list of the files you open.

When you report a problem, you decide what to write. Problem reports are public GitHub issues, so we never ask for the document itself.

## Reporting a problem

Report problems on GitHub: https://github.com/zain-ul-abdain/plain-viewer/issues/new/choose

1. Sign in to GitHub (a free account is enough).
2. Choose **Problem report**. The form (the same questions as `BETA-FEEDBACK.md`) opens ready to fill in.
3. Give it a short title, for example "Excel file shows wrong dates", fill in what you can, and click **Create**.

One problem per report is easiest for us. Before creating a new one, have a quick look at the existing issues; if someone already reported the same thing, add a comment there instead.

**Issues are public:** anyone on the internet can read them. Do not include names, addresses, account numbers or anything else private, in the text or in screenshots.

Most helpful:

- What you did, step by step, and what you expected to happen.
- The exact message in the status line at the bottom of the window, or in any message box.
- A screenshot (press Windows+Shift+S, select the area, then paste it into the issue with Ctrl+V). Check it shows nothing private.
- The file type and roughly how big it is. **Please do not attach the document itself** unless it contains nothing private: attachments on issues are public too. If you can, make a harmless copy that shows the same problem.

Good news is useful too: tell us which of your files looked right.
