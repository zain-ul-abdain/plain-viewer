# Release notes

## Next version (not yet released)

- **Excel workbooks protected without a password to open** (structure-protected .xls and .xlsx files, which Excel saves encrypted with a fixed password) now open without asking for a password.
- A password-protected Word, Excel or PowerPoint file that was changed after it was protected is now refused with a clear message.
- **EPUB books use their own fonts** when they include them.
- **Excel binary workbooks (.xlsb)** open: values, saved formula results, number formats, column widths, merged cells and frozen panes.
- **Conditional formatting written as formulas** (such as =$B2>100 or =MOD(ROW(),2)=0) is now shown in .xlsx, .xls and .ods files, worked out from the values saved in the file.
- **More chart types**: radar, bubble and stock charts, and combined charts such as columns with a line over them.
- **SmartArt diagrams** in Excel workbooks are shown, as Excel last drew them.
- **EMF and WMF pictures** in spreadsheets (.xlsx, .xls and .ods) are now shown: logos and drawings pasted from other programs no longer go missing.
- **Syntax colours** for code and data files: C, C#, Java, JavaScript, PHP, Visual Basic, CSS, web and project files, JSON, XML, YAML and INI files show comments, strings, keywords and numbers in colour (plain text in high contrast).

## 0.9.0 — beta: passwords for older Office and OpenDocument files (October 2026)

Install over any earlier beta; nothing needs uninstalling first.

- **Password-protected older Office files and OpenDocument files** open after you type the password: .doc, .xls, .ppt, .odt, .ods, .odp and their templates.
- Fixed: a password-protected OpenDocument file saved by current LibreOffice was described as damaged, and a password-protected .ppt file was not recognised as protected.

## 0.8.0 — beta: web pages, EPUB books, code files and password-protected Office files (October 2026)

Install over any earlier beta; nothing needs uninstalling first.

- **Web pages and books**: .html, .htm and .xhtml web pages, saved web archives (.mht, .mhtml) and EPUB books open in the viewer. Scripts, frames and anything stored outside the file are removed first and never run or load; pictures saved inside the file are shown. Books show their chapters in reading order with chapter navigation. Books protected with DRM are refused with a clear message.
- **Source code and project files** open as plain text: .c .h .cs .java .js .vb .css .php .asp .aspx .razor .config .csproj .sln. Nothing in them runs.
- **Tab-separated files** (.tsv) open in the grid.
- **Password-protected Word, Excel and PowerPoint files** open: Plain Viewer asks for the password and opens the file with it. The password is used only to open the file and is never saved. (Older .doc, .xls and .ppt files and OpenDocument files with passwords still cannot be opened.)
- **Older templates and flat spreadsheets**: Word and Excel 97–2003 templates (.dot, .xlt), OpenDocument templates (.ott, .ots) and flat OpenDocument spreadsheets (.fods).

## 0.7.0 — beta: shapes, chart labels and patterned fills in spreadsheets; safer links (October 2026)

Install over any earlier beta; nothing needs uninstalling first.

- About shows when your copy was built (date and time), so you can tell versions apart. The "Development preview" words are gone from the status line. Asked for by Zain.
- Markdown zoom: spacing now zooms with the text, so tables, lists, quotations and code blocks keep their proportions from 50% to 300% (table rows used to stay tall when zoomed out, and code blocks stayed narrow when zoomed in). Code blocks sit at the left like the text, and table header rows are bold. Reported by Zain.
- Safer links: a link is now shown and opened in its encoded form (spaces and quotes become %20 and %22), so a crafted link in a document cannot pass extra instructions to your browser or email program. Found by a security review of the code.
- Clearer messages when a file has been moved or deleted, cannot be read because of its permissions, or is locked by another program, for every kind of file (PDFs and pictures used to show "an unexpected problem").
- Spreadsheet shapes and text boxes (.xlsx, .xls and .ods): notes, callouts, arrows, lines and grouped shapes drawn over the cells now show, with their colours and text.
- Spreadsheet charts show their axis titles and data labels (values, pie percentages and category names), in .xlsx, .xls and .ods.
- Spreadsheets: patterned cell fills (grey shades, stripes, grids and crosshatching) now show, in .xlsx and .xls.
- Spreadsheets: colours and borders given to whole columns or rows now show in their empty cells too (.xlsx, .xls and .ods).
- Clearer message when the document worker stops unexpectedly (for example at its memory limit), instead of "an unexpected problem".

## 0.6.0 — beta: row heights and conditional formatting in spreadsheets (September 2026)

Install over any earlier beta; nothing needs uninstalling first.

- **Excel row heights** (.xlsx, .xls and .ods): rows keep the heights saved in the file, so wrapped text shows in full lines and large text is no longer squeezed; pictures and charts sit on the real rows. Sheets with more than 10,000 rows keep even rows.
- **Conditional formatting** in .xlsx and .ods files, and the value rules of .xls files: highlight rules (greater than, between, text contains, blanks, errors, top and bottom, above average, duplicates), colour scales, data bars and icon sets, worked out from the values saved in the file. Rules written as formulas, and rules about today's date, are not shown; a note counts them.
- A tidier toolbar: the text encoding and CSV delimiter lists appear only for files they apply to (the encoding for text, CSV, Markdown and data files; the delimiter for CSV), instead of greyed out everywhere else.

## 0.5.0 — beta: pictures and charts in .xls and .ods (September 2026)

Install over any earlier beta; nothing needs uninstalling first.

- **Pictures and charts in .xls and .ods files**, like those in .xlsx. Linked pictures stored outside the file are never loaded.
- Fixed: in .xls files, sheet settings saved after an embedded chart (such as frozen panes) were lost; in .ods files, text inside shapes was added to the cell's text.
- Release installers are built by GitHub Actions, ready for code signing through SignPath once the project is accepted.

## 0.4.0 — beta: Excel pictures and charts, iPhone photos, formatting in .xls and .ods (September 2026)

Install over any earlier beta; nothing needs uninstalling first.

### New

- **Pictures and charts in Excel workbooks (.xlsx and its variants)**, placed where the workbook puts them. Charts are drawn from the values saved with them (column, bar, line, area, pie, doughnut and scatter); chart sheets show their chart filling the window. Linked pictures stored outside the file are never loaded, and the status line says so.
- **iPhone photos (.heic, .heif)**, decoded with Windows' own decoder inside the restricted worker. They need Microsoft's "HEIF Image Extensions" and "HEVC Video Extensions" from the Microsoft Store (many PCs have them already); without them Plain Viewer says which to install. Nothing is downloaded by Plain Viewer.
- **Cell formatting in .xls and .ods**: fonts, colours, fills, borders and alignment, as for .xlsx.
- **Long .xls and .ods sheets** are no longer cut at 10,000 rows: rows past that are kept on disk and loaded as you scroll, as for .xlsx.

### Still not shown

Conditional formatting, shapes and text boxes, row heights; charts and pictures in .xls and .ods; less common chart kinds (a note says so).

## 0.3.0 — beta: older Office files, OpenDocument, RTF, TIFF; logo (September 2026)

Install over any earlier beta; nothing needs uninstalling first.

### New file types

- **Word 97–2003 (.doc), PowerPoint 97–2003 (.ppt), OpenDocument text and presentations (.odt, .odp) and Rich Text (.rtf)**, shown as pages or slides like .docx and .pptx. Before conversion, a private copy is cleaned of everything that could fetch content from elsewhere: linked pictures, templates, fetching fields and linked objects, including web addresses and network shares. In testing, the converter did fetch a linked picture from an uncleaned .doc; with the cleaning, no request was made.
- **Excel 97–2003 (.xls) and OpenDocument spreadsheets (.ods)** in the spreadsheet grid, with sheet tabs, number formats, column widths, merged cells, hidden rows, columns and sheets, and frozen panes. They are read directly and show the values saved in the file; formulas are never recalculated.
- **TIFF pictures (.tif, .tiff)**, including multi-page scans, shown as pages with thumbnails.
- A clear message for password-protected files, very old Word 6.0/95 and Excel 5.0/95 files, and files with the wrong ending.

### Also new

- A logo and app icon (window, taskbar, installer and Start menu). The title bar now says "Plain Viewer" and About says "beta".

### Still not shown

Cell formatting in .xls and .ods; Excel charts, pictures, conditional formatting and row heights; HEIC pictures.

## 0.2.0 — beta: more formats, Excel formatting, thumbnails (September 2026)

Install over any earlier beta; nothing needs uninstalling first.

### New file types

- **Pictures:** JPEG, PNG, GIF, BMP, icons, WebP, AVIF and SVG. Zoom, fit, and rotate (view only; the file never changes). Phone photos appear the right way up. Pictures are decoded inside the same sandbox as PDFs; an SVG is only ever shown as a picture, so anything active in it never runs and nothing it points to is loaded. A cut-short picture shows what it can, with a notice.
- **Office templates, shows and files with macros:** .dotx .docm .dotm, .ppsx .potx .pptm .potm .ppsm, .xlsm .xltx .xltm. Macros are removed or ignored and never run, and the status line says so.
- **Data files as plain text:** JSON, XML, log, INI and YAML files. They are never interpreted: XML entities and references stay exactly as written.
- "Open with" is offered for all of these (the installer option lists them); your default apps still do not change.

### Improvements

- **Excel formatting:** fonts (bold, italic, underline, strikethrough, colour, size, typeface), fill colours, borders, alignment and indent, including theme colours. Text on a coloured fill stays readable in dark mode. Columns keep the workbook's widths, long text spills over empty neighbouring cells as in Excel, and hidden columns take no space.
- **Page thumbnails** for PDF and Word documents: the Thumbnails button beside the page number (remembered).
- **CSV:** up to 16,384 columns (was 512).

### Still not shown

Excel charts, pictures, conditional formatting and row heights (wrapped text shows its first line); TIFF and HEIC pictures; older .doc/.xls/.ppt, OpenDocument and RTF files (planned for 0.3.0).

## 0.1.1 — beta update (September 2026)

Fixes from the first beta feedback. Install over 0.1.0; nothing needs uninstalling first.

- **Excel:** in sheets with a frozen top row, the row number of that row (and the empty corner above the row numbers) no longer scrolls away when you scroll right; frozen columns inside frozen rows stay on top as well.
- **Window size and position are remembered**, including whether the window was maximized. A position on a monitor that is no longer connected is ignored, and a second window opens slightly offset.
- **The theme choice (System, Light or Dark) is remembered.**
- Both are kept in `%LOCALAPPDATA%\PlainViewer\settings.json`, which never contains file names or document contents and is removed on uninstall.

## 0.1.0 — private beta (September 2026)

The first version for testers. Installer: `PlainViewer-Setup-0.1.0-x64.exe`, Windows 11 x64, not code-signed. Guide for testers: [BETA.md](BETA.md).

### What it does

- Opens PDF, Word (.docx), Excel (.xlsx), PowerPoint (.pptx), CSV, text and Markdown files for reading. Never changes the original file or writes beside it.
- Works fully offline: no uploads, usage data, crash reports or update checks. Everything it needs is in the installer, including the .NET runtime, LibreOffice 26.2.6 (for Word and PowerPoint), PDF.js and Microsoft's WebView2 Runtime installer.
- PDF and Word: continuous pages, page number and go-to-page, fit to page and fit to width. PowerPoint: one large slide with thumbnails, previous and next.
- Excel: sheet tabs, saved values and cached formula results (never recalculated; "Result unavailable" when a formula has no saved result), number and date formats, column widths, merged cells, frozen panes. Hidden sheets stay hidden.
- Text and CSV: encoding and delimiter detection with manual override; quoted and multi-line CSV fields; leading zeros kept.
- Markdown: rendered or source view, tables, task lists, code blocks. Embedded HTML shown as text; pictures and local files never loaded.
- Search with next and previous (match count where available), zoom, copy, light and dark themes with a manual choice, keyboard shortcuts, drag and drop, one window per document.
- Large files: CSV and text files up to 1 GB and Excel sheets with hundreds of thousands of rows are read from disk as you scroll; long searches show progress and can be cancelled.
- Safety: documents are read by a separate low-integrity process with memory and time limits; macros, scripts, external links and remote pictures, fonts and templates are never used; links ask before opening in the browser. Optional Windows Firewall rules block the document-reading processes from the network.
- Installs per user without administrator rights; optional "Open with" entries that never change your default apps; clean uninstall.

### Known limitations

- Word and PowerPoint layout is close to Microsoft Office but not identical (fonts the PC lacks are substituted; long documents can shift by a page). Animations, transitions, sound and video do not play.
- Excel cell formatting (colours, fonts, borders), charts, pictures and conditional formatting are not shown.
- No PDF page thumbnails; large text files have no word wrap; CSV is limited to 512 columns.
- Not supported: .doc, .xls, .ppt, .rtf, .odt, .ods, .odp and macro-enabled or template files.
- Not yet checked: Narrator, display scaling above 100%, ARM64 PCs.
- Unsigned: Windows SmartScreen warns before installing, and Smart App Control may block it.

Full details: [SUPPORT.md](SUPPORT.md). Test evidence: [TEST-RESULTS.md](TEST-RESULTS.md).
