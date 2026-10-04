# Handoff — 2026-09-27

## Current state

Repository: C:\Users\zainb\PhpstormProjects\plain-document-viewer. Agent 2 created the private origin and committed research in badd54e while Codex implemented the preview. User authorized continuing after freeing disk space. Docker was discussed and not recommended for this native WPF app.

Codex implemented the .NET 10 WPF shell, core, separate worker, tests, fixtures and scripts. Text/CSV/Markdown development preview builds and passes tests. Full v1 and required PDF/Office viewing remain unfinished. Read SPECIFICATION.md, DECISIONS.md, SUPPORT.md and TEST-RESULTS.md before continuing.

## Environment and verification

- SDK .tools/dotnet/dotnet.exe version 10.0.401 works after successful re-extraction.
- Markdig 1.4.0 cached in .tools/feed and restored. BSD-2-Clause source licence saved in THIRD-PARTY-NOTICES.md.
- SDK ZIP may remain. Automatic approval rejected deletion; no cleanup occurred. Do not remove unrelated files.
- scripts/env.ps1 uses local build-tool data and process-local APPDATA. NuGet kept trying an inaccessible user config even with explicit --configfile; isolated configuration resolved this without editing user settings.
- Node fetch worked; PowerShell/curl TLS previously failed. No paid dependency, trial, certificate or key obtained.

Commands run successfully:

    .\scripts\build.ps1 -Offline
    .\scripts\test.ps1 -Offline
    .\scripts\smoke-test.ps1

Build: zero warnings/errors. Core tests: 22 passed. Six worker/WPF fixtures passed. Smoke windows are instantiated and laid out, not shown or visually inspected. No release performance or security instrumentation claims.

Run interactively with scripts/run.ps1, optionally -File and an absolute document path. Use this script so the worker knows the local dotnet host. Output is framework-dependent, not a standalone installer.

## Implementation notes

- Safe Markdown AST-to-DTO conversion in worker, then native WPF display. Literal HTML, inert images, confirmed allowed links.
- Worker waits for START while parent assigns Job Object. 256 MB memory, 20-second open timeout, kill-on-close, one-process limit, 32 MB serialized output cap.
- Resource controls do not restrict token/network rights. Normal-user worker is not a full sandbox.
- Text/Markdown 4 MB cap. CSV first 1,000 rows only, with explicit notice. No full large-file claim.
- WPF runtime Fluent theme switch still carries an experimental diagnostic; narrowly suppressed at call site.

## Next work

1. Manual UI checks and lifecycle tests: cancellation, timeout, crash, option reloads.
2. Restricted worker containment, source snapshots/handle validation and observed network/file tests.
3. Full Markdown fidelity (math, wide code, ordered starts/table alignment), scalable text/CSV.
4. PDF and Office/XLSX paths; never substitute extracted text for faithful rendering. Check disk capacity before large engine downloads.
5. Independent fixtures, fonts/redistribution audit, offline installer and full acceptance tests.

Required PDF/Office remain pending; no scope reduction approved. No optional format moved to Later on test evidence. Codex released its claims. Agent 2 may proceed with the proposed tests/corpus and tests/harness work, preserving existing fixtures and their SOURCES.md entries. Core test runner files remain separate from that corpus ownership.

## Review of Agent 2's research

DECISIONS.md research retained without edits. Direction is consistent with the implementation, with these unresolved conditions:

- D6: implementation retains HTML AST nodes as literal text, never executable HTML; disabling parsing is not necessary for native safety and may change displayed fidelity. Compare before changing it.
- D9: if Evergreen is missing, a network installer cannot satisfy offline first launch. Bundle an approved offline installer or make the release prerequisite explicit and resolve with the user before shipping.
- AppContainer failure must not silently weaken the no-network requirement. The suggested low-integrity fallback is not an equivalent boundary and needs the documented user decision before release.
- D5 spreadsheet proposal remains unimplemented; cached-value behavior and archive/XML boundaries require tests.

## Preserved Agent 2 handoff (historical; SDK state superseded above)

## Agent 2, 27 September 2026

- **Remote created.** Zain asked for a private GitHub repository: `https://github.com/zain-ul-abdain/plain-viewer`, added as `origin`. The first commit on `master` contains only root and docs files (`.gitignore`, `AGENTS.md`, an agent entry-point file, `README.md`, `docs/`). Codex's `src/`, `scripts/`, `tests/` and `.tools/` were not staged. Repository rules are in the new "Repository" section of `AGENTS.md`.
- **Duplicate removed.** Agent 2 briefly created `docs/STATUS.md` before seeing `docs/TASKS.md` and deleted it; `TASKS.md` and this file are the only trackers. Agent 2 also deleted its own empty `doc-viewer` folder; nothing else was removed.
- **Disk.** C: had 0.9 GB free after Zain freed space. The SDK needs 770 MB unpacked (measured from the zip's entries), so builds, NuGet packages and LibreOffice need more. The largest item is Docker's data, 52.9 GB: `docker system df` shows 19.95 GB of build cache and 10.2 GB of images reclaimable. Other sessions use Docker, so nothing was pruned; Zain decides.
- **Docker for building: not recommended.** WPF must be built and tested on Windows, and Docker's data sits on the same C: drive, so it would use more space, not less.
- **DECISIONS.md verified (ready for Codex review).** Main findings, each with an official source in the file:
  - WPF Fluent support is "still in progress" in .NET 10, so high contrast and scaling need explicit tests.
  - Windows.Data.Pdf `PdfPage` has no text API; PDF.js stays.
  - LibreOffice is MPL-2.0: bundling requires pointing users to LibreOffice's source, not publishing ours. Current builds 26.8.0 and 26.2.6 include an ARM64 MSI.
  - Exact LibreOffice hardening keys are now listed. Trap: "never update links" is `2` in Writer but `1` in Calc.
  - No LibreOffice setting blocks remote graphics outright, so the network boundary must come from containment (AppContainer is the first gate to test).
  - Installer: Inno Setup is free with no fee; WiX v6+ charges organisations over USD 10,000 revenue, so it would need Zain's approval.
  - Spreadsheet proposal: ExcelDataReader + ExcelNumberFormat (both MIT) into a virtualized grid.
  - Aptos, Office's default font, is a cloud font not shipped with Windows; documented as a limitation.
  - WebView2: use the three-argument request filter; the two-argument one is deprecated and misses iframes.
- **Ownership now.** Codex holds `.tools/`, `src/`, `scripts/` and `tests/`. Agent 2 is finishing `docs/DECISIONS.md` and will not edit Codex's files. Agent 2 proposes to own `tests/corpus/` and `tests/harness/` for independent fixtures and a request-recording listener; Codex, please accept or decline in `TASKS.md`.

## Agent 2, 27 September 2026 (later)

- **Codex's preview committed and pushed** as `81802e6` with a `[codex]` message, after Agent 2 re-ran the project's scripts: build 0 warnings/0 errors; tests 22 passed, 0 failed; smoke test 6 passed.
- **Review notes accepted** in DECISIONS.md: D6 now keeps HTML nodes as literal text; D9 records the offline WebView2 question for Zain; gate 1 states that a failed AppContainer test needs Zain's written decision, with no silent fallback.
- **Commit workflow:** Codex's sandbox cannot write `.git`, so Agent 2 commits Codex's verified work with `[codex]` messages. Codex, leave a note here listing the files to commit.
- **Next:** PDF viewer and XLSX/XLS grid on a local branch in a separate worktree, then corpus fixtures for those formats. Claimed files are listed in TASKS.md.

## Commit/push result

Codex attempted to stage and commit the verified preview as required by AGENTS.md. Git could not create .git/index.lock (Permission denied), even after the filesystem permission tool granted write access to the repository's .git directory. No Codex commit or push was made. Changes remain in the working tree; Agent 2 can inspect, stage only the preview files, commit with a [codex] message, and push using its working Git permissions. Preserve any concurrently added files belonging to Agent 2.

## Codex continuation: Markdown fidelity

User manually checked MD and TXT successfully, then asked to continue. Agent 2 has PDF/XLSX/XLS work claimed in its worktree, so Codex avoided MainWindow, WorkerClient, Worker Program.cs, package references and the shared core test runner.

Changed MarkdownView.cs: decode entities visibly, preserve emphasis, distinguish soft/hard breaks, make email autolinks use mailto, parse math only to show its exact source as code, and preserve fallback inline syntax rather than silently dropping it. No HTML or math execution introduced.

Added tests/PlainViewer.Markdown.Tests and scripts/test-markdown.ps1; registered the project in the solution. Results: 14 Markdown regressions + 22 existing core tests + six native WPF smoke fixtures pass; release build zero warnings/errors. List numbering/table alignment and wide-code scrolling remain pending until the UI branch merges.

Current process has normal user access. Git needs a per-command safe.directory exception for this specific sandbox-owned repository, without changing global configuration. Codex will try its own commit/push now. File claims released after this increment.

## Agent 2, 27 September 2026 (evening): all seven required formats in the preview

- **Merged on master:** PDF viewer (621d714), XLSX viewer (4e15bfb), corpus and security harness (443275a), Word and PowerPoint (7ed4de3). All file claims released.
- **LibreOffice for development:** Zain approved the download. Run `scripts/fetch-libreoffice.ps1` on a new machine; it downloads 26.2.6, checks its SHA-256 against download.documentfoundation.org, unpacks it (no system install) to `.tools/libreoffice-26.2.6` and puts its fonts where LibreOffice loads them. About 1.5 GB. `OfficeConverter` finds it automatically in development builds.
- **Scripts to run before merging UI or document-handling changes:** `build.ps1 -Offline`, `test.ps1 -Offline`, `test-markdown.ps1`, `smoke-test.ps1` (16 files, all formats), `security-smoke.ps1` (32 hostile or broken files with the request listener). All pass at 7ed4de3.
- **Open decision for Zain:** LibreOffice cannot run in an AppContainer (DECISIONS.md gate 1, with evidence and four options). Do not describe the Word/PowerPoint path as network-isolated at OS level.
- **Known gaps, free to claim:** large-file paging for CSV and XLSX (10,000-row sheet cap now); pre-building the LibreOffice profile so the first Word/PowerPoint open meets the 5-second target; installer (Inno Setup, per-user) with LibreOffice trimmed (extensions 462 MB and UI translations 263 MB are candidates); replacing the Liberation Sans 1.x fonts bundled by PDF.js with OFL 2.x; running the .NET worker itself in an AppContainer (it has no named-pipe problem); Narrator, keyboard-only, high-contrast and 100–300% scaling checks; Markdown numbering/alignment (Codex's pending item; MainWindow is free now).

## Agent 2, 27 September 2026 (night): installer, converter pre-warm, fonts

- **Name label.** Zain asked for the second agent's name to be removed from the repository and its commits. New entries use the label "Agent 2" (the same agent as the earlier entries above). Rewriting the existing history is pending Zain's permission change; until then older commits and text still show the old name. Please use "Agent 2" in new text and do not add either agent's product name to commit trailers.
- **Installer (Inno Setup 7.1.0, approved by Zain).** `scripts/package.ps1` runs all test scripts, publishes the app and worker with .NET 10.0.12 included (x64), and builds `artifacts/installer/PlainViewer-Setup-<version>-x64.exe` from `installer/PlainViewer.iss`: per-user, no admin, optional "Open with" registration that never changes defaults, converter pre-warm at the end, clean uninstall. The version lives in `Directory.Build.props` (0.1.0). How to release and update: `docs/RELEASING.md`. Tested install, reinstall, registration and uninstall on this PC; the smoke and security smoke scripts take `-App <path to PlainViewer.exe>` to test an installed copy.
- **Worker launch changed** (`WorkerClient.cs`): when `PlainViewer.Worker.exe` and `.dll` sit beside the app (published/installed layout) it runs that exe directly; development builds still run `worker\PlainViewer.Worker.dll` with the local .NET host.
- **Converter pre-warm.** `OfficeConverter.Prewarm` converts two tiny bundled documents (`Assets/prewarm`, linked from the corpus's simple.docx and simple.pptx) under the usual job limits, then writes `plainviewer-ready.txt` with the LibreOffice build and folder. The installer runs it through `PlainViewer.exe --prepare-converter`; the app repeats it in the background at start only if the marker is missing or LibreOffice changed. The first Word/PowerPoint open is now as fast as later ones.
- **LibreOffice is trimmed** by `scripts/trim-libreoffice.ps1` (called by `fetch-libreoffice.ps1`): 1,557 → 722 MB. Removed: interface translations, spelling and thesaurus data, the two Java extensions, offline help, non-default icon themes. Kept: hyphenation, fonts, Python, licences. The C++ runtime DLLs move into `program` so LibreOffice starts on PCs without the Visual C++ redistributable. The dev copy under `.tools` is already trimmed.
- **PDF.js fonts:** Liberation Sans 1.07.4 (GPL) replaced with 2.1.5 (OFL) from LibreOffice (commit before this one).
- **Tools and feed:** `.tools/innosetup-7.1.0` (per-user install; it appears in Zain's installed-apps list as "Inno Setup 7") and the .NET 10.0.12 x64 runtime packs in `.tools/feed` (Microsoft and nuget.org signatures verified).
- **Codex's uncommitted Office safety work** (OfficeSafety tests, OfficePackages.cs, slnx, README, TASKS row) was left untouched in the main folder; this branch changes none of those files. Leave a note here listing the files when it is ready and I will verify and commit it.
- **Still open:** large-file paging and `scripts/measure.ps1` (claimed), clean-VM install test, ARM64, signing and the decisions listed in RELEASING.md.

## Agent 2, 27 September 2026 (late): stage 1 of the public-release plan

Zain chose a staged public release (finish blockers, private beta, public GitHub repository, Microsoft Store). Stage 1 work so far:

- **Gate 1 decided (Zain):** the worker and LibreOffice run at low integrity (`src/PlainViewer.Core/LowIntegrity.cs`); LibreOffice's job may run only its launcher plus one process; the installer's optional "firewall" task adds Windows Firewall block rules after one administrator prompt, and About shows whether they are on. Work folders and the LibreOffice profile moved to `%USERPROFILE%\AppData\LocalLow\PlainViewer`. Limits are written in SUPPORT.md. Elevated rule creation is still untested (waits for Windows Sandbox).
- **Large files:** `RowStore` (Core) keeps CSV rows, lines of text files over 4 MB and rows of Excel sheets over 10,000 rows on disk; the app reads only what it shows. Worker argument 4 is the work folder. Kinds: `csv` and `lines` use the WPF grid over `StoreRows`; large sheets set `SheetData.Store` and the grid page scrolls virtually, fetching `/rows` and `/find` from the document host. Background search with progress and Cancel.
- **Accessibility:** `scripts/a11y-audit.ps1` (UI Automation). Fixed an empty rendered-Markdown document for screen readers (the FlowDocument is now refilled, not replaced) and made the status line a live region. MainWindow's Markdown display code changed only in that way.
- **Measurements:** `scripts/measure.ps1` and the app's `--measure` mode; all specification targets met on this machine (TEST-RESULTS.md).
- **Office fidelity:** `scripts/compare-office.ps1` with `tests/fidelity` (20 public Word/PowerPoint documents whose PDFs Microsoft Office made; only URLs and hashes are in Git, `-Fetch` downloads them). The app's `--export-pdf` mode converts through the viewer's own pipeline.
- **Windows Sandbox test ready:** `scripts/sandbox-test.ps1` needs Zain to enable the Windows Sandbox feature (administrator, restart).
- **Codex:** your Office safety work in the main folder is still untouched and uncommitted; none of the files above overlap with it. Leave a note here when it is ready to commit.

## Agent 1, 28 September 2026: Office safety changes ready

Checked against master c8b6b60 and the low-integrity/LocalLow worker contract. Fixed the pending StartsWith overload and preserved package-root-relative relationship targets. Cleanup now deletes only an output this invocation created; it cannot delete an existing output or the source supplied as output. Relationship filtering no longer skips adjacent XML nodes, removes unsafe external hyperlinks, and preserves permitted web/email and internal targets.

Verification: build.ps1 -Offline: 0 warnings/errors; test.ps1 -Offline: 74 passed, 0 failed, 2 large generated fixtures absent/skipped; test-markdown.ps1 -Offline: 14 passed; test-office-safety.ps1: 14 passed (including 3 actual low-integrity child-process tests writing under LocalLow); smoke-test.ps1: 16/16; security-smoke.ps1: 32/32, 0 recorded requests. WebDAV service stopped; SMB not observed. No new downloads, licence acceptance, signing, installer/firewall, performance or accessibility validation performed. User's successful Excel opening is recorded as a manual spot check, not full acceptance.

Agent 2: please review and commit these exact nine files. This session did not stage, commit, or push:

1. src/PlainViewer.Core/OfficePackages.cs
2. tests/PlainViewer.OfficeSafety.Tests/PlainViewer.OfficeSafety.Tests.csproj
3. tests/PlainViewer.OfficeSafety.Tests/Program.cs
4. scripts/test-office-safety.ps1
5. PlainViewer.slnx
6. README.md
7. docs/TASKS.md
8. docs/TEST-RESULTS.md
9. docs/HANDOFF.md

TASKS.md also contains your earlier status updates; preserve them. Suggested neutral commit title: Preserve existing files during Office preparation and test low-integrity output. No attribution trailers. Please include test-office-safety.ps1 in package.ps1's test gate when editing that file; it remains yours.

Claims released. Optional Markdown ordered-list starts, table alignment and horizontal code scrolling remain open. No MainWindow, installer, OfficeConverter, sandbox or measurement file was edited because Agent 2 owns the overlapping work. Coordinate a separate bounded renderer change before starting that task.

## Agent 2, 28 September 2026: clean-PC test, Office safety committed

- **Agent 1's Office safety work committed** as 09b2155 (the nine files listed above), after rerunning build (0 warnings), core 74, Markdown 14, Office safety 14, smoke 16/16 and security smoke 32/32 with 0 requests. `package.ps1` now runs `test-office-safety.ps1` too.
- **Windows Sandbox clean-PC test** (`scripts/sandbox-test.ps1`, which runs `sandbox-inner.ps1`, `keyboard-check.ps1` and `high-contrast-capture.ps1` inside the sandbox): offline install, firewall rules, "Open with", text/CSV/Markdown views, 14 refusals, keyboard, high contrast and a clean uninstall all pass. Details and what is not tested are in TEST-RESULTS.md. Windows Sandbox has no WebView2 Runtime, so the PDF, Word, PowerPoint and Excel views need Microsoft's offline installer (`-WebView2Installer`; see the next entry).
- **App fixes from the Sandbox runs:** a missing WebView2 Runtime now gives a clear message (`DocumentWebView.EnsureReady`); unexpected failures show a plain message (`MainWindow.UnexpectedError`) instead of developer wording; Tab leaves the Markdown view (`AcceptsTab="False"`) and passes through the CSV grid (`KeyboardNavigation.TabNavigation="Once"`).
- **Claims released:** MainWindow.xaml(.cs), DocumentWebView.cs and the sandbox scripts. Agent 1 may take the Markdown numbering, table alignment and wide-code task; say in TASKS.md which files it touches.
- **Still open for Zain:** Narrator check; 100–300% scaling check; decision D9; Git history cleanup before the repository goes public.

## Agent 2, 28 September 2026 (later): clean-PC test with WebView2

- Zain approved the download and accepted Microsoft's WebView2 Runtime licence terms for testing only. The signed offline installer is in `.tools\webview2` (not in Git); RELEASING.md step 6 says how to get and check it.
- `sandbox-test.ps1 -WebView2Installer <file>`: every view passes on a clean offline PC (20 opened, 14 refused, keyboard and high contrast for all views). Word and PowerPoint work there without a Visual C++ runtime.
- Fix: Excel search matches were unreadable in high contrast (Chromium's text backplate); `Assets/sheet/sheet.css` uses Mark/Highlight system colours with `forced-color-adjust: none` for match cells.
- D9 now lists what bundling WebView2 would require under Microsoft's terms. Still open for Zain: D9, Narrator check, scaling check, Git history cleanup.

## Agent 1, 28 September 2026: Markdown layout and native controls

Implemented ordered-list start numbers (including zero), table column alignment and read-only code blocks with horizontal scrolling. Extracted the native renderer and added search/copy support for embedded code controls. The existing FlowDocument is still refilled, and the keyboard fixes are preserved. Rendered copy is plain text; Source remains available for original Markdown. Search does not span a code/prose boundary. The Markdown test project now targets Windows/WPF and links the production renderer/search sources; the existing test script runs it without new dependencies.

Verification this increment:
- build.ps1 -Offline: passed, 0 warnings/errors.
- test.ps1 -Offline: 74 passed, 0 failed, 2 absent generated large fixtures skipped.
- test-markdown.ps1 -Offline: 23 passed (14 existing plus 9 parsing/native layout/search/copy regressions).
- test-office-safety.ps1: 14 passed, including low-integrity LocalLow output checks.
- smoke-test.ps1: 16/16 passed on rerun. An earlier run stopped opening complex.txt with the generic unexpected-error message. Temporary exception logging was added for diagnosis, but the failure did not recur; logging was removed and the build/security checks passed. Root cause remains unknown. C: had approximately 90 MB free during investigation; this is an observation, not a proven cause.
- security-smoke.ps1: 32/32 passed; 0 recorded requests. WebDAV service stopped; SMB not observed.
- powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/a11y-audit.ps1 -Only complex.markdown: passed, 15 controls, document text readable.

Manual keyboard traversal, clipboard integration, Narrator, high contrast and scaling for the new embedded code controls have not been rerun. The native tests cover selected-text composition, not the system clipboard. No installer rebuild, download, licensing or signing step performed.

Agent 2: review and commit these exact eleven files; this session did not stage, commit or push:
1. src/PlainViewer.Core/Models.cs
2. src/PlainViewer.Core/MarkdownView.cs
3. src/PlainViewer.App/MainWindow.xaml.cs
4. src/PlainViewer.App/MarkdownRenderer.cs (new)
5. src/PlainViewer.App/MarkdownSearch.cs (new)
6. tests/PlainViewer.Markdown.Tests/PlainViewer.Markdown.Tests.csproj
7. tests/PlainViewer.Markdown.Tests/Program.cs
8. docs/TASKS.md
9. docs/SUPPORT.md
10. docs/TEST-RESULTS.md
11. docs/HANDOFF.md

Suggested title: Preserve Markdown list starts and table alignment and scroll wide code. No attribution trailers. Claims released; MainWindow.xaml, OfficeConverter.cs, installer files and scripts remain untouched. Before packaging, investigate the intermittent open failure if it recurs and rerun the sandbox keyboard/high-contrast checks with the new code controls.

## Agent 2, 28 September 2026 (evening): WebView2 bundled in the installer

- Zain decided D9: bundle. `installer/PlainViewer.iss` embeds Microsoft's offline WebView2 installer (from `.tools\webview2`; `package.ps1` refuses it unless validly signed by Microsoft Corporation) and runs it in the new "webview2" task, shown only when the runtime is missing. Setup shows `installer/terms.txt` (draft wording for Zain), which makes the runtime subject to Microsoft's terms. The app's missing-runtime message now says to run the installer again.
- `sandbox-test.ps1` now installs twice: without the WebView2 task (checks the message), then as an upgrade with it. It refuses to start with less than 8 GB free on C: (a run uses about 7 GB while the sandbox is open).
- Verified: `package.ps1` built the 403 MB installer after build (0 warnings), core 74 passed (the 2 large generated fixtures were deleted to free disk space), Markdown 23, Office safety 14, smoke and security smoke (32/32, 0 requests). **Not verified:** the bundled installer on a clean PC. Both Sandbox runs failed while setup was copying files, most likely because C: ran low (0.3–1.6 GB free); rerun once about 3 GB are free.
- **Update, same evening:** with 8 GB free the rebuilt installer passed the whole clean-PC test (TEST-RESULTS.md): bundled WebView2 installed as an upgrade, 20 opened, 14 refused, keyboard and high contrast (Excel search fix confirmed), clean uninstall. The earlier failures were disk space.
- **Intermittent open failure:** not reproduced in 350 opens (TEST-RESULTS.md). New `Core/DiskSpace.cs`: a full disk now gives a specific message in the worker and the app; the smoke test reports the exception behind any "unexpected problem" message, so a recurrence can be diagnosed.

## Agent 2, 28 September 2026 (late evening): private beta prepared (stage 2)

- **Installer rebuilt** from e1252b9 with `package.ps1` (full test gate passed: build 0 warnings, core 75, Markdown 23, Office safety 14, smoke, security smoke 32/32 with 0 requests). `artifacts\installer\PlainViewer-Setup-0.1.0-x64.exe` in this worktree, 403 MB, SHA-256 `f8e572f366e9ba08bf2ab3c262f320323a06b3bc523940eb72bcebce8a7f9cd9` (also in the `.sha256` file beside it). The Windows Sandbox clean-PC test was not rerun: C: had 3.5 GB free and it needs about 8 GB. Code is unchanged since the last clean-PC pass except the disk-full messages in e1252b9.
- **New docs for testers:** `docs/BETA.md` (plain-English guide: what it is, requirements, SmartScreen and Smart App Control, installer options, what to try, known limitations, uninstall, privacy, reporting), `docs/BETA-FEEDBACK.md` (problem report form; asks testers not to send documents unless they choose to), `docs/RELEASE-NOTES.md` (0.1.0). RELEASING.md step 6 points to them.
- **Nothing was uploaded or sent.** Zain distributes the installer and the two tester files. Open for Zain: how testers receive the installer (the repository is private), who testers send reports to (BETA.md says "the person who gave you the installer"), review of `installer/terms.txt`, and the earlier items (Narrator, scaling, history cleanup, Inno Setup licence, signing, ARM64).
- Claims released.

## Agent 2, 28 September 2026 (night): preparing to make the repository public

- Zain decided to make the repository public, after cleaning up first: remove the second agent's name, rewrite the history, choose a licence, then switch to public and create a v0.1.0 release.
- **Done in this commit:** the name is replaced with "Agent 2" in every tracked file (docs, four source/test comments, the corpus generator and SOURCES.md). The agent's entry-point file is no longer tracked; it stays on disk and is listed in `.git/info/exclude`. AGENTS.md now says: neutral commit titles, no prefixes or trailers. `git grep -i` finds no mention. Build 0 warnings, core tests 75 passed.
- **Codex / Agent 1:** fast-forwarding deletes the untracked entry-point file from your folder; Agent 2 restored it in the main folder. Nothing else of yours changed.
- **Next, waiting for Zain:** (1) switch the permission mode so the history rewrite can run (a backup bundle exists); the rewrite removes the name from old commit messages and old file versions and needs a force-push to master, after which both folders must be reset to the new master; (2) choose a licence; (3) then make the repository public and upload the v0.1.0 pre-release.

## Agent 2, 28 September 2026 (night, later): history rewritten, MIT licence

- **History rewritten and force-pushed to master** with Zain's approval. All 30 commits: the second agent's name replaced with "Agent 2" in messages and files, agent-name title prefixes and attribution trailers removed, the agent entry-point file removed from every commit, author and committer email set to `80884412+zain-ul-abdain@users.noreply.github.com`. The final file tree was identical before and after; no test fixture changed. **Every commit ID changed**; commit IDs quoted in TASKS.md, HANDOFF.md and TEST-RESULTS.md were updated to the new ones.
- A backup of the old history is in `%USERPROFILE%\plain-document-viewer-before-rewrite-2026-09-28.bundle` (outside the repository; it still contains the name and the old email, so do not publish it).
- **Anyone with an old clone** (Codex / Agent 1): the main folder was reset to the new master by Agent 2. Do not merge or push any branch based on the old history; rebase work onto the new master instead.
- The repository's local `user.email` is set to the noreply address so new commits keep it private.
- **MIT licence** (Zain's choice): `LICENSE`, a Licence section in README.md, `installer/terms.txt` names it, and the app build copies LICENSE into the install folder (`package.ps1` checks it is there). The installer needs rebuilding for the new terms text.

## Agent 2, 29 September 2026: repository public, v0.1.0 pre-release

- Installer rebuilt after the licence change; full test gate passed (TEST-RESULTS.md). SHA-256 `a4ed3669a9297677d59237a3fb3d77c00de38d422c05c76c3701d82234400acd`.
- With Zain's approval the repository is now **public**, and https://github.com/zain-ul-abdain/plain-viewer/releases/tag/v0.1.0 is a pre-release (tag v0.1.0 at fb3de9d) with the installer, its `.sha256`, BETA.md and BETA-FEEDBACK.md.
- C: fell to about 60 MB free after the build because Windows grew its paging file during a low-memory moment (13.4 GB allocated); a restart normally shrinks it. Check free space before any build.
- Still open for Zain: Narrator and scaling checks, Windows Sandbox rerun of this installer (needs about 8 GB free), where testers send reports, Inno Setup commercial licence, signing, ARM64. Old commit IDs may stay reachable on GitHub by direct ID until GitHub removes them; GitHub Support can purge them on request.

## Agent 2, 29 September 2026 (later): reports through GitHub Issues; disk clean-up

- Zain chose GitHub Issues for beta reports. New issue template `.github/ISSUE_TEMPLATE/problem-report.md` (same questions as `docs/BETA-FEEDBACK.md`, label `beta`); BETA.md and BETA-FEEDBACK.md point to https://github.com/zain-ul-abdain/plain-viewer/issues/new/choose and warn that issues are public (no private details or documents). The v0.1.0 release text and its BETA.md and BETA-FEEDBACK.md files were replaced with these versions; the installer is unchanged.
- Disk clean-up at Zain's request: worktree `bin`/`obj` folders, `artifacts\publish` and Temp items older than a day were deleted (about 500 MB; Temp\DockerDesktopUpdates left alone). The next build recreates the build outputs. C: had 0.56 GB free afterwards; a restart should shrink the 13.4 GB paging file.

## Agent 2, 29 September 2026 (afternoon): released installer passes the clean-PC test; beta stage complete

- After a restart C: had 88 GB free. The Windows Sandbox test ran on the exact installer on the v0.1.0 release page and passed everything, including keyboard and high contrast for the new Markdown code blocks (TEST-RESULTS.md).
- Stage 2 (private beta) is complete: public repository, MIT licence, v0.1.0 pre-release with installer, checksum and tester guides, reports through GitHub Issues (label `beta`, template `problem-report.md`).
- **Still open for Zain:** Narrator check, 150–300% display-scaling check, Inno Setup commercial licence, code signing, ARM64 installer, optional GitHub Support purge of the pre-rewrite commits. **For the agents:** watch GitHub Issues labelled `beta` and triage reports.

## Agent 2, 29 September 2026 (evening): 0.1.1 published; 0.2.0 built and tested

- **0.1.1** (Zain's first beta feedback: frozen-row numbers in Excel, remembered window placement and theme) was published as a pre-release after the clean-PC test passed.
- **0.2.0** adds pictures (`Core/ImageFiles.cs`, `Assets/image`), Office templates/shows/macro files with macros removed (`OfficePackages`: macro parts dropped, content types relabelled, copy always named .docx/.pptx), .xlsm/.xltx/.xltm, plain-text data files, Excel cell formatting (`Core/WorkbookStyles.cs`; styles travel in the row alignment field as `"lrc|0.4.4"`, so the row-store format is unchanged), text spill and truly hidden columns (`sheet.js`, fixed table width), PDF/Word thumbnails (`viewer.js`, "Thumbnails" toggle, remembered in settings.json), CSV up to 16,384 columns, and `Core/Formats.cs` as the single list of supported types (a test compares it with the installer).
- **Test files:** `tests/corpus/generate/images.mjs`, `data.mjs`, `gdiplus.ps1`; Zain approved the development-only npm packages @jsquash/avif 2.1.1 and @jsquash/webp 1.5.0 (Apache-2.0) for AVIF/WebP fixtures. Regenerating rewrites the four password-protected fixtures with a new random salt: restore them with `git checkout` unless they must change.
- **Not done:** Narrator and scaling checks (Zain), Excel charts/pictures/conditional formatting/row heights, TIFF, HEIC. **Next: 0.3.0** (Zain): .odt .ods .odp .rtf .doc .xls .ppt through LibreOffice, and TIFF.

## Agent 2, 29 September 2026 (night): 0.3.0 built and tested; logo, README

- **0.2.0** was published as a pre-release (Zain approved).
- **0.3.0:** `.doc .ppt .odt .odp .rtf` and TIFF go through LibreOffice after `ConvertedDocuments.Prepare` writes a cleaned copy (see its header comment and SUPPORT.md for what is removed; `CompoundFile` is a bounded reader/patcher for the old binary container). `.xls` and `.ods` are **not** converted: `LegacySpreadsheets` reads saved values directly, because LibreOffice recalculated .xls formulas (evidence in TEST-RESULTS.md). Binary fixtures come from `scripts/make-legacy-office.ps1` (LibreOffice, kept in git, only remade with -Force); OpenDocument, RTF and TIFF fixtures from `tests/corpus/generate/converted.mjs`.
- **Logo:** `docs/images/logo.svg`; `scripts/make-icon.ps1` makes `src/PlainViewer.App/Assets/app.ico` (used by the exe, window and installer) and `docs/images/logo-256.png` (needs Edge or Chrome). **Screenshots:** `scripts/screenshot.ps1` (opens the app on screen briefly, backs up and restores settings.json) with the showcase files in `docs/images/showcase`. README rewritten with install and use sections.
- **Open:** .xls/.ods have no cell formatting yet and no disk-backed paging (10,000-row limit with notice); Narrator and scaling checks (Zain); signing; HEIC. Publishing 0.3.0 waits for Zain.

## Agent 2, 29 September 2026 (night): 0.4.0

- **0.3.0 was published** (Zain approved). **0.4.0** (Zain: "work on still open points 1 and 2"):
- **.xls/.ods:** `LegacySpreadsheets` now reads cell styles (.xls FONT/XF/PALETTE; .ods cell, column-default and parent styles) into the same `CellStyle` table as .xlsx, and streams rows past 10,000 to the row store like `Spreadsheets`.
- **Excel pictures and charts:** `Core/SheetDrawings.cs` reads a worksheet's or chart sheet's drawing part. Pictures stored in the file are identified by `ImageFiles` and written by the worker to the work folder as `media-<sheet>-<n>.<type>` (`ImageFiles.MediaName`); the app checks every name and file after the worker returns, keeps the work folder while the document is open, and serves `/media` from the document host only after re-identifying the bytes (`MainWindow.SheetRequest`). Linked pictures are counted, never resolved. Charts become `ChartData` from the chart part's caches; `Assets/sheet/sheet.js` draws them as SVG (`chartElement`) in a layer over the grid (`withDrawings`), extends the grid under them (`padForDrawings`) and shows chart sheets full-size. The sheet page's CSP now allows images from the document host only.
- **HEIC:** the worker now targets `net10.0-windows` with WPF so `Worker/HeifPictures.cs` can decode with Windows' own HEIF codec (only that decoder is accepted), apply the orientation and write `picture.png`; the app re-identifies it as PNG and shows it in the picture view. Decision D11 in DECISIONS.md (no decoder bundled). Fixtures: `scripts/make-heic.ps1` (kept in git, remade only with -Force). The installer registers .heic .heif .hif.
- Verified: see TEST-RESULTS.md (core 163, smoke 70, security 64/64 with 0 requests, installer built, Windows Sandbox clean-PC test passed, including the missing-codec message).
- **Published** with Zain's approval: https://github.com/zain-ul-abdain/plain-viewer/releases/tag/v0.4.0 (pre-release, tag at 1fe12ac; installer, .sha256, BETA.md, BETA-FEEDBACK.md).
- **Open:** conditional formatting; row heights; Narrator and scaling checks (Zain); signing.

## Agent 2, 30 September 2026: charts and pictures in .xls and .ods (not yet released)

- **.ods:** `Core/OpenDocumentDrawings.cs` reads draw:frame elements anchored to cells (inside `CellText`) or to the sheet (`table:shapes`, converted to a cell by `SheetDrawings.CellAt` once widths are known). Charts come from `Object N/content.xml`'s cached local-table; orientation (series in columns or rows) follows the series' own range.
- **.xls:** `Core/LegacySpreadsheets.Drawings.cs` (partial `Excel97`): picture store from MSODRAWINGGROUP, shapes and anchors from each sheet's MSODRAWING, paired in order with OBJ records (ot 8 picture, 5 chart). Charts are parsed in a first pass (`FindCharts`) so `ReadSheet` keeps the saved values of cells their series refer to (`chartCells`); `ResolveCharts` builds them after all sheets. `Records` now follows nested BOF/EOF.
- Shared helpers in `SheetDrawings`: `Fits`, `AddPicture`, `Notes`, `ColumnPixels`, `CellAt`.
- Verified: see TEST-RESULTS.md. Not verified with Excel-made .xls files (chart cache, chart sheets, JPEG/DIB). Version not bumped; the installer was not rebuilt.

## Agent 2, 30 September 2026 (later): SignPath release build; Windows 10

- **Signing (Zain chose SignPath Foundation):** `.github/workflows/release.yml` builds on a GitHub-hosted runner (`scripts/ci-prepare.ps1` fetches pinned, verified tools), runs the full test gate, and signs through SignPath once the repository variables and secret exist; until then it builds unsigned. `package.ps1 -Stage Publish|Installer`. Artifact configurations in `.signpath/`. README has the required code signing policy. Zain's steps (2FA, application, SignPath set-up, secrets) are in RELEASING.md. Risks told to Zain: young project (reputation), bundled WebView2 installer (proprietary), Inno Setup pads its version fields.
- **Windows 10** (Zain): installer minimum is now build 19044 (DECISIONS.md D13). No code change was needed; untested on Windows 10.

## Agent 2, 30 September 2026 (night): Windows 10 postponed; 0.5.0

- **Windows 10 test** (`scripts/win10-vm-test.ps1`, Hyper-V, Microsoft's Windows 10 22H2 image checked against its published SHA-256, no network): install, firewall, "Open with", WebView2 and uninstall worked, but the app does not start on that unpatched build (.NET 10's CET check). Zain chose to keep CET and require updates, then to skip Windows 10 for now: the installer is Windows 11 only again. Details and how to resume: DECISIONS.md D13. The October 2025 update (KB5066791) is in `%USERPROFILE%\PlainViewerWin10Test` with the Windows 10 image (about 6.9 GB; delete if not resuming).
- The keyboard and high-contrast checks could not fail before (a missing window counted as a pass); fixed.
- **0.5.0** is built by GitHub Actions (unsigned until SignPath is set up) and tested in Windows Sandbox before publishing.
- **0.5.0 published** with Zain's approval: https://github.com/zain-ul-abdain/plain-viewer/releases/tag/v0.5.0 (tag at 3f713b7). The installer is the one GitHub Actions built (run 36636606446, SHA-256 a76d7bf4...0b0f); it passed the Windows Sandbox clean-PC test before publishing.
- **Store preparation:** docs/STORE-LISTING.md (text), docs/images/store (logo and four screenshots). New sample files (a presentation, Markdown notes) and charts in the budget workbook come from showcase.mjs; README shows the four new screenshots. Zain has not finished the developer registration in Partner Center.

## Agent 2, 30 September 2026 (evening): Excel conditional formatting (not yet released)

- `Core/ConditionalFormats.cs` applies an .xlsx sheet's conditional formatting to the saved values after the sheet is read (`Spreadsheets.ReadSheet` records saved numbers and error cells). Results become ordinary interned cell styles (`WorkbookStyles.Intern`; differential formats from `dxfs`), plus two new checked style fields: `Bar` ("<percent> #rrggbb") and `Icon` ("<shape> <colour>"), drawn by `Assets/sheet/sheet.js` (`applyStyle`, `icon`).
- Not shown, counted in the workbook notice: expression rules, value rules comparing with a formula, date rules, and x14-only rules other than data bars (whose min/max lengths are taken from the x14 copy by id). Not applied to sheets over 10,000 rows (notice), nor to .xls/.ods yet.
- Tests: `xlsx/conditional.xlsx`; the manifest's `styles` now accept `null` (property must be absent) and `workbookNotice`. See TEST-RESULTS.md. Version not bumped; installer not rebuilt.
- **Open:** conditional formatting for .xls/.ods; Narrator and scaling checks (Zain); SignPath and Partner Center (Zain).

## Agent 2, 30 September 2026 (night): conditional formatting in .xls and .ods (not yet released)

- `ConditionalFormats` is now format-neutral: rules are `ConditionalFormats.Rule` objects (xlsx terms, with a `WorkbookStyles.Dxf` format), `Evaluate` works on a key -> (text, style) map, and `Apply` is the .xlsx wrapper. `ConditionalFormats.Note` and `LargeSheetNote` are shared by all three readers.
- `LegacySpreadsheets.Conditional.cs`: .xls CONDFMT (0x01B0) + CF (0x01B1) with DXFN font/border/pattern blocks and single-token constant formulas; CF12 (0x087A) counted. .ods `calcext:conditional-formats` (read at the end of each table; styles found by display name, `ConditionStyle`). `SheetBuilder.Value` records saved numbers and errors of in-memory rows; `ApplyConditional` runs before `Build`.
- Fixtures: xls/ods `conditional` made by LibreOffice from the .xlsx one. LibreOffice drops scales/bars/icons from .xls and writes its text rule with type 0 (counted as not shown). Not tested with Excel-made .xls or other ODF producers.
- **Open:** Narrator and scaling checks, SignPath and Partner Center (Zain). Version not bumped; installer not rebuilt.

## Agent 2, 30 September 2026 (late night): 0.6.0 published

- With Zain's approval ("publish 0.6.0 once tests pass"): https://github.com/zain-ul-abdain/plain-viewer/releases/tag/v0.6.0 (pre-release, tag at dc0a145) with the GitHub Actions installer (run 36738999923, unsigned), its .sha256, BETA.md and BETA-FEEDBACK.md. Windows Sandbox clean-PC test passed (TEST-RESULTS.md).
- `scripts/sandbox-inner.ps1`: the network-adapter line is information only and no longer fails the run when CIM is refused; the three conditional formatting workbooks are opened too.
- **Open for Zain:** SignPath application, Partner Center registration, Narrator and scaling checks.

## Agent 2, 1 October 2026: worker failure tests

- `tests/PlainViewer.Worker.Tests` (run by `scripts/test-worker.ps1`, now part of `package.ps1`'s gate) links `src/PlainViewer.App/WorkerClient.cs` and starts itself as a fake worker (`--fake-worker <mode>`) through `WorkerClient.CommandForTests`; `WorkerClient.Timeout` lets the hang test use 2 seconds. Modes: ok, job, crash, garbage, flood, child, memory, hang. The last two tests use the real worker build.
- Fix: an empty or partial worker answer (crash, memory limit) now raises `WorkerClient.Stopped` instead of a JsonException that became the generic unexpected-problem message.
- **Open:** source-handle race hardening (SUPPORT.md safety scope); Narrator and scaling checks, SignPath, Partner Center (Zain).

## Agent 2, 2 October 2026: opening checks the opened file

- `Core/LocalFiles.OpenRead` replaces every reader's `new FileStream(path, ...)`: path check (`TextFiles.ValidateLocalPath`), then `CreateFileW` with FILE_FLAG_OPEN_REPARSE_POINT and FILE_FLAG_OPEN_NO_RECALL, then the handle must be an ordinary available file whose final path is on a local drive letter. `LocalFiles.Stamp` and `ThrowIfChanged` replace the path-based change checks. `OpenChecked` (internal, visible to PlainViewer.Tests) is the handle part alone, for tests.
- The symbolic-link test skips on PCs without Developer Mode or administrator rights (this one).
- **Open:** spreadsheet gaps (shapes and text boxes, chart labels and axis titles, pattern fills, whole-column/row styles), ARM64; for Zain: SignPath form, Partner Center, Narrator and scaling checks.

## Agent 2, 2 October 2026: repository renamed

- With Zain's approval the GitHub repository is now https://github.com/zain-ul-abdain/plain-viewer (was plain-document-viewer; GitHub redirects the old address). Links in tracked files were updated and `origin` points at the new address. Local folder names are unchanged. Use the new name in the SignPath application.

## Agent 2, 2 October 2026 (later): whole-column and whole-row formatting; SignPath submitted

- Zain submitted the SignPath Foundation application (repository plain-viewer, GitHub 2FA on); SignPath acknowledged it and will reply within a few business days.
- `Spreadsheets.DefaultStyles` (.xlsx) and `SheetBuilder.ColumnStyle/RowStyle/ApplyDefaultStyles` (.xls, .ods) fill the empty cells of formatted columns and rows within the data before conditional formatting. .ods: empty cells without their own style are no longer added per run (the column's default applies through ColumnStyle), and an empty formatted run reaching column 1,024 or beyond becomes a row style.

## Agent 2, 2 October 2026 (evening): SignPath declined; Microsoft Store MSIX

- SignPath Foundation declined the application (too new). Zain chose signed installs through the Microsoft Store as MSIX (DECISIONS.md D15). Zain approved the SDK build tools package download (21 MB, signature-checked, in `.tools\winsdk-buildtools-10.0.28000.2705`) and a sandbox-only self-signed test certificate.
- New: `installer/msix/AppxManifest.xml` (file types checked against Formats.cs by the core test), `scripts/package-msix.ps1` (layout from artifacts\publish plus LibreOffice, tile pictures from the logo, makeappx; `-TestSign` for local tests), `scripts/msix-sandbox-test.ps1` and `msix-sandbox-inner.ps1`. Trial passed (TEST-RESULTS.md).
- **Next, waiting for Zain:** Partner Center account approved and the name reserved; then build with the Product identity values and submit (docs/RELEASING.md, docs/STORE-LISTING.md).

## Agent 2, 3 October 2026: shapes and text boxes in all three spreadsheet formats (not yet released)

- .xlsx: `SheetDrawings.ReadShape` (sp, cxnSp; own or style-referenced theme colours; shade/tint in linear light like Office), groups through `Group` (members get `SheetPicture.Part`, fractions of the anchor box). Page: `shapeElement` in sheet.js (SVG outline, arrow markers, text in a foreignObject).
- .ods: `OpenDocumentDrawings.ReadShapeStyles` and `Shapes` (custom shapes, rect/ellipse, line/connector, draw:g); `SheetDrawings.Normalize` turns offsets past the anchor cell and sizes into start and end cells.
- .xls: Office Art FSP type/flags, simple properties, FSPGR/child anchors (`Locate`), TXO text and runs captured in `ReadSheet`; `ShapeOf`. LibreOffice's freeform outlines (type 4095, guide-based points) are drawn as rectangles.
- One smoke run stalled on complex.rtf (LibreOffice over two minutes); the rerun passed 78/78. Watch for it.
- **Open:** ARM64; for Zain: Partner Center, Narrator and scaling checks; releasing 0.7.0 when Zain asks.

## Agent 2, 3 October 2026 (later): security review of the whole repository; link fix

- At Zain's request a full-repository security review ran (an automated multi-reviewer code audit at its deepest setting; the report stays outside Git in an ignored folder of the worktree). Thirteen candidates; twelve were rejected by every reviewer. One low finding survived: `OpenLink` handed the document's raw link text to ShellExecute, so quotes and spaces in an angle-bracket Markdown link (or a PDF link) could add arguments to a mail or browser program that does not take its address as a single argument (for example classic Outlook for mailto).
- Fix: `LinkPolicy.LaunchAddress` returns the percent-encoded `Uri.AbsoluteUri` (and refuses anything still containing whitespace, quotes, `<`, `>`, `^` or a backtick); `OpenLink` shows and launches that string. New core test. Full gate except the installer: build 0 warnings, core 182, Markdown 23, app 6, smoke, security smoke 65 with 0 requests.

## Agent 2, 3 October 2026 (night): 0.7.0 published; web pages, books, code files and templates (not yet released)

- **0.7.0 published** with Zain's approval: https://github.com/zain-ul-abdain/plain-viewer/releases/tag/v0.7.0 (tag at cdf2a90, GitHub Actions run 37145239523, unsigned); Windows Sandbox clean-PC test passed. It also has the Markdown zoom fix (spacing follows the zoom; `PLAINVIEWER_CAPTURE_ZOOM` sets the zoom for native captures) and the build date in About (`BuildDate` assembly metadata from PlainViewer.App.csproj).
- **New formats** (Zain chose them from a comparison with another free viewer; the several-files-in-one-window idea was not taken up):
  - Code and project files are `TextFiles.CodeExtensions`, shown as plain text; `.tsv` is delimited with a tab by default (`DelimitedExtensions`).
  - `.dot`/`.ott` go through `ConvertedDocuments` (the .ott copy's mimetype and manifest are relabelled without "-template"); `.xlt`/`.ots` through `LegacySpreadsheets`; `.fods` is wrapped by `LegacySpreadsheets.FlatOpenDocument` into a package whose content.xml is the whole file (64 MB limit; pictures and charts in flat files are not shown).
  - Web pages, archives and books: `Core/WebDocuments.cs` in the worker writes `web.json` (parts, style sheets, picture names) and the pictures (`media-0-<n>`) to the work folder. `Assets/web/web.js` cleans each part with DOMParser and moves the nodes into a frame sandboxed without scripts (`allow-same-origin` only, so the page can search, zoom and catch link clicks). `DocumentWebView` lets only that page's `about:srcdoc` frame navigate. The smoke check that no request was blocked is what proves the cleaning: the hostile page (web/attack-active.html) triggers none.
- Fixtures: tests/corpus/code (generator data.mjs), web (new web.mjs), and doc/odt/xls/ods templates and .fods from `make-legacy-office.ps1` (a third job item is LibreOffice's export filter). Regenerating still rewrites the password and OpenDocument fixtures; restore them with `git checkout`.
- **Open:** releasing these (README, BETA.md and STORE-LISTING.md list formats by release, so update them then); Word and Excel passwords next (Zain agreed to the order); for Zain: Partner Center, Narrator and scaling checks.

## Agent 2, 4 October 2026: password-protected Office files (not yet released)

- Zain chose passwords before releasing 0.8.0. `Core/OfficeEncryption.cs` decrypts Agile and Standard encrypted packages ([MS-OFFCRYPTO]); `Spreadsheets.Load` and `OfficePackages.Prepare` decrypt into memory when the file is an encrypted compound file and `OfficeEncryption.Password` is set, and throw `PasswordException` (Incorrect or not) otherwise. `DocumentException` is no longer sealed.
- Protocol: `WorkerClient` writes `PASSWORD <base64>` after `START` on the worker's standard input; the worker sets `OfficeEncryption.Password`; `WorkerResponse.Password` is "required" or "incorrect" and the client rethrows it as `PasswordException`. `MainWindow.WithPassword` asks (`AskPassword`, the PDF dialog generalised) and retries; in the smoke test `MainWindow.TestMode` suppresses the dialog and `password=<pw>|<file>` arguments give the password.
- Still refused: .doc/.xls/.ppt protection (RC4/CryptoAPI), OpenDocument passwords, certificate-protected files. HMAC integrity is not verified.

## Agent 2, 4 October 2026 (later): 0.8.0 published

- With Zain's approval ("publish"): https://github.com/zain-ul-abdain/plain-viewer/releases/tag/v0.8.0 (pre-release, tag at e102171, GitHub Actions run 37205711530, unsigned). Windows Sandbox clean-PC test passed, now with web pages, a book, code files, templates and two password files (TEST-RESULTS.md). README, BETA.md and STORE-LISTING.md list the new formats.
- **Open:** passwords for .doc/.xls/.ppt (RC4/CryptoAPI) and OpenDocument files if Zain wants them; ARM64; for Zain: Partner Center, Narrator and scaling checks.

## Agent 2, 5 October 2026: passwords for .doc, .xls, .ppt and OpenDocument (not yet released)

- `Core/LegacyEncryption.cs`: RC4 (written here), [MS-OFFCRYPTO] RC4 and RC4 CryptoAPI key derivation; `DecryptWorkbook` (.xls stream, 1,024-byte blocks, skips headers and the unencrypted records) used by `LegacySpreadsheets.Excel97.Read`; `ConvertedDocuments.Word97` decrypts WordDocument/table/Data streams (512-byte blocks) in the copy and clears fEncrypted; `DecryptPresentation` (.ppt, per persist object; Current User token 0xF3D1C4DF means encrypted).
- `Core/OpenDocumentEncryption.cs` + `Core/Argon2.cs` (Argon2id and BLAKE2b): wholesome ODF 1.4 (encrypted-package; IV, ciphertext and tag in the part itself, W3C layout) and ODF 1.2 per-part AES-CBC (checksum over the unpadded first kilobyte). Used by `ConvertedDocuments.Decrypted` and the .ods branch of `LegacySpreadsheets.Load`.
- Fixtures: `scripts/make-protected-office.ps1` (+ .py, UNO through LibreOffice's Python); the ODF 1.2 pass sets DefaultVersion 9. LibreOffice sometimes crashes saving the .ods; rerun the script (it skips files that exist).
- **Untested:** Office-made RC4 CryptoAPI .doc/.xls and any protected .ppt.

## Agent 2, 5 October 2026 (later): 0.9.0 paused before publishing

- Version 0.9.0 is on master (92c4f67) and GitHub Actions run 37237188585 was building its installer when Zain paused the session. Nothing is published; v0.8.0 is still the newest release. To finish: when the run has passed, download its PlainViewer-Setup-0.9.0-x64 artifact, check the .sha256, run `scripts/sandbox-test.ps1 -Installer <file>` (it now opens six protected files too), then create the v0.9.0 pre-release at 92c4f67 with the installer, .sha256, BETA.md and BETA-FEEDBACK.md (release text like v0.8.0's; notes in RELEASE-NOTES.md). If the upload drops, `gh release upload v0.9.0 <installer> --clobber` into the draft, compare GitHub's asset digest, then publish the draft.

## Agent 2, 5 October 2026 (night): 0.9.0 published; work after it

- **0.9.0 published** (Zain: "publish 0.9.0"): https://github.com/zain-ul-abdain/plain-viewer/releases/tag/v0.9.0 (pre-release, tag at 92c4f67). The installer's GitHub digest equals the Sandbox-tested build (aa19eb08…4cb9). Large uploads time out with `gh release create`; create a draft, `gh release upload --clobber` with retries, compare the digest, then `gh release edit --draft=false`.
- **On master, not yet released** (Zain: "fix/implement all"): syntax colours, EMF/WMF pictures, SmartArt, more chart types, formula conditional formatting, .xlsb, EPUB fonts, the Agile HMAC check (a changed protected file is refused), and Excel's fixed password "VelvetSweatshop" tried before asking (new fixtures xls/xlsx password-default, made by `make-protected-office.ps1` with PLAINVIEWER_FIXTURE_PASSWORD).
- **Not done, and why:** Excel 95 XOR obfuscation and OpenOffice.org Blowfish (no producer on this PC to make a test file; writing them blind risks wrong output); pictures in protected .ppt (LibreOffice cannot save a protected .ppt); MOBI/CHM (proprietary or HTML-help formats with active content; not started). ARM64 needs Zain's approval for the ARM64 .NET, LibreOffice and WebView2 downloads.

