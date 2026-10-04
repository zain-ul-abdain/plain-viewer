# Microsoft Store listing (draft)

Text for Partner Center, one section per field, ready to paste. Nothing has been submitted. The app goes to the Store
as an **MSIX package** (DECISIONS.md D15): the Store signs it, so no certificate is needed. How to build and test the
package: docs/RELEASING.md, "Microsoft Store package (MSIX)". Partner Center
shows each field's length limit; the texts below are written to fit the usual limits (description well under 10,000
characters, each feature under 200, at most 7 search terms), but check them there.

Keep this file in step with README.md and docs/SUPPORT.md: the Store listing must not promise more than the app does.

## Product name

Plain Viewer

If the name is already reserved by someone else: **Plain Document Viewer**.

## Category

Productivity. Pricing: free. Markets: all.

## Description

Plain Viewer opens your documents so you can read them: open a file, read it, close it. It works offline, needs no Microsoft Office, and never changes your files.

WHAT IT OPENS
• PDF documents
• Word and other documents: .docx, .doc, .odt, .rtf, and Word templates and macro-enabled files
• Excel and other spreadsheets: .xlsx, .xls, .ods, and Excel templates and macro-enabled files
• PowerPoint and other presentations: .pptx, .ppt, .odp, and shows, templates and macro-enabled files
• Pictures: JPEG, PNG, GIF, BMP, icons, WebP, AVIF, SVG, TIFF (including multi-page scans) and iPhone photos (HEIC/HEIF)
• Web pages (.html, saved .mht pages) and EPUB books, with scripts removed
• Text and data: .txt, .csv, .tsv, Markdown, .json, .xml, .log, .ini, .yaml, and source code files as text
• Password-protected Word, Excel, PowerPoint and OpenDocument files, after you type the password

READ COMFORTABLY
• Word and PDF files as pages, with page thumbnails, a page number box, fit to width and fit to page.
• PowerPoint as one slide at a time with a strip of slides.
• Excel with sheet tabs, column widths, merged cells, frozen rows and columns, cell colours, fonts and borders, pictures and charts.
• Markdown formatted, or as its source.
• Search with a match count, zoom, copy, light and dark themes, Windows contrast themes, and full keyboard use.

SAFE BY DESIGN
• Never changes, moves or renames the files you open.
• Macros, scripts and embedded programs never run. Files with macros open with the macros removed or ignored.
• Nothing is loaded from the internet or from network shares: no remote pictures, fonts or templates.
• Links never open by themselves. Clicking a web or email link shows its full address and asks first.
• Spreadsheets show the values saved in the file; formulas are never recalculated.
• Documents are read by a separate, restricted process with time and memory limits.

PRIVATE
Plain Viewer never uploads anything, has no account or sign-in, sends no usage data or crash reports, and never checks for updates.

GOOD TO KNOW
• Presentations are shown as still slides: animations, transitions, audio and video do not play.
• Word and PowerPoint files are laid out by the bundled LibreOffice, so pages can differ slightly from Microsoft Office, especially when a document uses fonts your PC lacks.
• iPhone photos (HEIC) need Microsoft's HEIF Image Extensions and HEVC Video Extensions from the Microsoft Store.

Plain Viewer is free and open source (MIT licence): https://github.com/zain-ul-abdain/plain-viewer

## What's new in this version

Pictures and charts in older Excel (.xls) and OpenDocument (.ods) spreadsheets, like those in .xlsx. Linked pictures stored outside a file are never loaded. Fixes: in .xls files, sheet settings saved after an embedded chart (such as frozen panes) were lost; in .ods files, text inside shapes appeared in the cell's text.

## Product features

1. Opens PDF, Word, Excel, PowerPoint, OpenDocument, RTF, pictures, text, CSV and Markdown files
2. Works offline and without Microsoft Office
3. Never changes, moves or renames the files you open
4. Macros, scripts and embedded programs never run
5. Loads nothing from the internet or network shares; links ask before opening
6. Page thumbnails, page number box, fit to width and fit to page
7. Excel sheet tabs, cell formatting, frozen panes, pictures and charts; formulas never recalculated
8. Search with match count, zoom and copy
9. Light, dark and Windows contrast themes; full keyboard use
10. No account, no ads, no usage data, no update checks

## Search terms

document viewer; PDF viewer; Word viewer; Excel viewer; PowerPoint viewer; offline viewer; read only

## Copyright and trademark information

© 2026 Zain ul abdain. Plain Viewer is released under the MIT licence. Microsoft Word, Excel and PowerPoint are trademarks of the Microsoft group of companies; Plain Viewer is not affiliated with Microsoft. LibreOffice is a trademark of The Document Foundation.

## Additional license terms

Paste the text of installer/terms.txt (the same terms the installer shows). In the Store the installer runs silently, so this field is where users see them, including that installing the Microsoft Edge WebView2 Runtime makes it subject to Microsoft's own licence terms.

## Privacy policy

URL: https://github.com/zain-ul-abdain/plain-viewer#privacy-and-safety

Plain Viewer collects no personal data and sends nothing over the network. (The README's "Privacy and safety" and "Code signing policy" sections say this; if Partner Center wants a page that is only a privacy policy, add docs/PRIVACY.md with the same statement and link to it.)

## Website and support

- Website: https://github.com/zain-ul-abdain/plain-viewer
- Support contact: https://github.com/zain-ul-abdain/plain-viewer/issues/new/choose (problem reports are public; the template asks people to leave out private details and documents)

## System requirements

- Minimum: Windows 11 (x64), 4 GB of memory, about 1.3 GB of free disk space.
- Recommended: 8 GB of memory.
- ARM64 PCs: not tested.

## Age rating (IARC questionnaire)

Answer "no" to every content question: no violence, sexual content, gambling, drugs, crude humour, user-to-user communication, sharing of location or personal data, in-app purchases, or unrestricted web browsing (links open in the user's browser only after asking). The expected rating is the lowest (3+ / Everyone).

## Package (MSIX submission)

- Upload `artifacts\msix\PlainViewer-<version>.0-x64.msix`, built with the identity from Partner Center's Product identity page (docs/RELEASING.md). The Store signs it. Architecture: x64. Language: English. Minimum Windows: Windows 11 (10.0.22000).
- Restricted capability `runFullTrust`, justification to paste: "Plain Viewer is a desktop (WPF) application. It opens each document in a separate worker process that lowers itself to low integrity, and converts Word and PowerPoint files with a bundled copy of LibreOffice, also at low integrity; both need a full-trust desktop process. The app makes no network connections."
- "Open with" entries come from the package; Windows never makes Plain Viewer a default app by itself.

## Notes for certification (for Microsoft's testers)

Plain Viewer is a read-only document viewer. After installing, open any document from the Start menu app with "Open file" (Ctrl+O), or right-click a file in File Explorer and choose Open with > Plain Viewer. It needs no account and makes no network connections; a sample set of documents is in the tests/corpus folder of the GitHub repository. It uses the Microsoft Edge WebView2 Runtime that Windows 11 includes, and a bundled, trimmed LibreOffice (MPL-2.0) that lays out Word and PowerPoint files.
## Images

In docs/images/store (made by scripts/screenshot.ps1 and scripts/make-icon.ps1 -StoreLogoOnly from the sample files in docs/images/showcase, which tests/corpus/generate/showcase.mjs makes):

- Store logo: logo-300.png (300 × 300).
- Screenshots, 1902 × 1004 (the Store needs at least 1366 × 768), with suggested captions:
  1. screenshot-1-document.png: "Word documents as pages, with page thumbnails"
  2. screenshot-2-spreadsheet.png: "Excel workbooks with their formatting and charts"
  3. screenshot-3-presentation.png: "PowerPoint slides with a slide strip"
  4. screenshot-4-markdown-dark.png: "Markdown notes, here in the dark theme"

The status line at the bottom of the window is cropped off: it shows how long a file took to open, which on a busy PC misrepresents typical times.

## Before submitting

1. The MSIX package passes scripts\msix-sandbox-test.ps1 (with a local test identity), then is rebuilt with the Partner Center identity.
2. A Partner Center developer account (identity verification), and the name reserved.
3. Upload the images above.
4. Whether to mention "beta" in the listing while releases are pre-releases on GitHub.
