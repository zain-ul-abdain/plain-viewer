using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PlainViewer.Core;
namespace PlainViewer.App;

public partial class MainWindow : Window
{
    private DocumentView? document;
    private string? currentPath;
    private CancellationTokenSource? loading;
    // CSV and large text: rows on disk in a work folder written by the worker; deleted when replaced or closed.
    private RowStore? rowStore;
    private Dictionary<int, RowStore> sheetStores = [];   // large spreadsheet sheets, by sheet position
    private string? rowStoreFolder;
    private StoreSearch? storeSearch;
    // Shown when opening fails in a way the app has no specific message for (the smoke test treats it as a failure).
    private const string UnexpectedError = "The document could not be opened because of an unexpected problem. Try again, or try another copy of the file.";
    private Exception? unexpected;   // behind the last UnexpectedError; reported by the smoke test only
    private string UnexpectedDetail => unexpected is null ? "" : $" ({unexpected.GetType().Name}: {unexpected.Message})";
    private int storeMatch = -1;
    private double zoom = 1;
    private string lastQuery = "";
    private int matchIndex = -1;
    private WindowState savedState;
    private double openSeconds;
    public MainWindow()
    {
        InitializeComponent(); PreviewKeyDown += WindowKeyDown;
        MarkdownDisplay.SizeChanged += (_, e) => MarkdownRenderer.ResizeCode(MarkdownDisplay.Document, e.NewSize.Width);
        MarkdownDisplay.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        {
            if (!MarkdownDisplay.IsKeyboardFocused) return; // Nested code controls own their native selection.
            try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, MarkdownSearch.SelectedText(MarkdownDisplay)), true); }
            catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "The clipboard is busy. Try copying again."; }
            e.Handled = true;
        }, (_, e) =>
        {
            if (!MarkdownDisplay.IsKeyboardFocused) return;
            e.CanExecute = !MarkdownDisplay.Selection.IsEmpty; e.Handled = true;
        }));
        // The theme is chosen here, not in XAML: a SelectedIndex in XAML is applied late and would undo the saved choice.
        if (AppSettings.Enabled) { RestoreSettings(); Closing += (_, _) => SavePlacement(); }
        else ThemeChoice.SelectedIndex = 0;
        Closed += (_, _) => { loading?.Cancel(); ReplaceRowStore(null, null, null); };
        // The status line is a live region: screen readers announce loading, errors and search results as they change.
        var statusText = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler announce = (_, _) => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(Status)
            ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        statusText.AddValueChanged(Status, announce);
        Closed += (_, _) => statusText.RemoveValueChanged(Status, announce);
        WebPane.StateChanged += ShowWebStatus;
        WebPane.FindResult += (current, total, finished) => { if (finished) Status.Text = total == 0 ? "No matches." : $"Match {Math.Max(current, 1)} of {total}."; };
        WebPane.LinkRequested += address =>
        {
            if (LinkPolicy.CanOpen(address)) OpenLink(address);
            else Status.Text = "This link was not opened because it is not a web or email address.";
        };
        WebPane.AskPassword = AskPdfPassword;
    }
    private static string Choice(ComboBox box) => ((ComboBoxItem)box.SelectedItem).Content.ToString()!;
    private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    private static bool IsWorkbook(string path) => Spreadsheets.IsWorkbook(path) || LegacySpreadsheets.Handles(path) || Path.GetExtension(path).Equals(".xlsb", StringComparison.OrdinalIgnoreCase);
    // Shown as pages through the converter: Office files, OpenDocument text and presentations, RTF, .doc, .ppt and TIFF.
    private static bool IsOffice(string path) => OfficePackages.IsOfficeDocument(path) || ConvertedDocuments.KindOf(path) is "word" or "slides" or "pages";
    private static bool IsPicture(string path) => ImageFiles.IsImage(path);
    private static bool UsesWebPane(string path) => IsPdf(path) || IsWorkbook(path) || IsOffice(path) || IsPicture(path) || WebDocuments.Handles(path);
    private bool InWebPane => document?.Kind is "pdf" or "sheet" or "word" or "slides" or "image" or "web";
    private bool Paginated => document?.Kind is "pdf" or "word" or "slides";
    internal async Task<string> VerifyRefusedAsync(string path)
    {
        if (UsesWebPane(path))
        { WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000; ShowActivated = false; ShowInTaskbar = false; Show(); }
        currentPath = path; await LoadCurrent();
        if (document is not null) throw new InvalidOperationException($"Expected {Path.GetFileName(path)} to be refused, but it opened.");
        if (string.IsNullOrWhiteSpace(Status.Text) || Status.Text == UnexpectedError)
            throw new InvalidOperationException($"{Path.GetFileName(path)} was refused without a specific message: {Status.Text}{UnexpectedDetail}");
        if (WebPane.BlockedRequests != 0) throw new InvalidOperationException($"The document view attempted {WebPane.BlockedRequests} blocked request(s).");
        return Status.Text;
    }
    internal async Task VerifyPreviewAsync(string path)
    {
        if (UsesWebPane(path) || Environment.GetEnvironmentVariable("PLAINVIEWER_CAPTURE_DIR") is { Length: > 0 })
        {
            // WebView2 needs a real window handle (and captures a drawn window), so the smoke test shows it off-screen.
            WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000; ShowActivated = false; ShowInTaskbar = false; Show();
        }
        currentPath = path; await LoadCurrent();
        if (document is null) throw new InvalidOperationException(Status.Text + UnexpectedDetail);
        if (InWebPane)
        {
            if (WebPane.Pages < 1) throw new InvalidOperationException("The document reported no pages or sheets.");
            if (document.Kind == "image")
            {
                // Pictures: a size was reported, search is switched off with a reason, and rotating turns the picture.
                if (WebPane.PictureWidth < 1 || WebPane.PictureHeight < 1) throw new InvalidOperationException("The picture reported no size.");
                if (FindBox.IsEnabled || FindBox.ToolTip is null) throw new InvalidOperationException("Search should be switched off, with a reason, for pictures.");
                var turned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnState() { if (WebPane.Rotation == 90) turned.TrySetResult(); }
                WebPane.StateChanged += OnState;
                try { WebPane.Rotate(1); await turned.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { WebPane.StateChanged -= OnState; }
                WebPane.Rotate(-1);
            }
            else if (document.Encoding == "TIFF picture")
            {
                if (FindBox.IsEnabled) throw new InvalidOperationException("Search should be switched off for TIFF pictures.");
            }
            else
            {
                var (_, total) = await PdfFind("Hello");
                if (total < 1) throw new InvalidOperationException("Search found no match for 'Hello'.");
            }
            if (EpubBook)
            {
                // Books: turning to the last page, back a chapter, zooming (the book is laid out again) and back to page 1.
                async Task WaitFor(Func<bool> condition, string failure)
                {
                    var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnState() { if (condition()) reached.TrySetResult(); }
                    WebPane.StateChanged += OnState;
                    try { if (!condition()) await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException) { throw new InvalidOperationException($"{failure} (page {WebPane.Page} of {WebPane.Pages}, chapter {WebPane.Chapter} of {WebPane.Chapters})."); }
                    finally { WebPane.StateChanged -= OnState; }
                }
                await WaitFor(() => WebPane.Chapters >= 1, "The book reported no chapters");
                WebPane.GoToPage(WebPane.Pages);
                await WaitFor(() => WebPane.Page == WebPane.Pages, "Going to the last page failed");
                if (WebPane.Pages > 1)
                {
                    int last = WebPane.Page;
                    WebPane.ChangeChapter(-1);
                    await WaitFor(() => WebPane.Page < last, "Going back a chapter should move to an earlier page");
                }
                if (Environment.GetEnvironmentVariable("PLAINVIEWER_CAPTURE_DIR") is { Length: > 0 } bookCaptures)
                { Directory.CreateDirectory(bookCaptures); await Task.Delay(500); await WebPane.Capture(Path.Combine(bookCaptures, Path.GetFileName(path) + ".later.png")); }
                ZoomBy(1);
                await WaitFor(() => WebPane.Scale > 1 && WebPane.Page >= 1 && WebPane.Page <= WebPane.Pages, "Zooming should lay the book out again");
                WebPane.GoToPage(1);
                await WaitFor(() => WebPane.Page == 1 && WebPane.Chapter == 1, "Page 1 should show the first chapter");
            }
            ZoomBy(1); ZoomBy(0);
            if (Environment.GetEnvironmentVariable("PLAINVIEWER_CAPTURE_DIR") is { Length: > 0 } captures)
            {
                // Optional visual evidence for manual review: one PNG per document (and per sheet).
                if (document.Kind is "pdf" or "word") ThumbnailsToggle.IsChecked = true;   // shows the thumbnail strip too
                Directory.CreateDirectory(captures); await Task.Delay(800);
                await WebPane.Capture(Path.Combine(captures, Path.GetFileName(path) + ".png"));
                for (int sheet = 2; document.Kind == "sheet" && sheet <= WebPane.Pages; sheet++)
                { WebPane.ChangeSheet(1); await Task.Delay(500); await WebPane.Capture(Path.Combine(captures, $"{Path.GetFileName(path)}.sheet{sheet}.png")); }
            }
            if (document.Kind == "sheet" && WebPane.Pages > 1) { WebPane.ChangeSheet(1); WebPane.ChangeSheet(-1); }
            await ExerciseControls();
            if (WebPane.BlockedRequests != 0) throw new InvalidOperationException($"The document view attempted {WebPane.BlockedRequests} blocked request(s).");
            return;
        }
        Measure(new Size(1100, 760)); Arrange(new Rect(0, 0, 1100, 760)); UpdateLayout();
        if (document.Kind == "markdown" && MarkdownDisplay.Document.Blocks.Count == 0) throw new InvalidOperationException("No Markdown blocks rendered.");
        if (document.Kind is "csv" or "lines" && CsvGrid.Items.Count != (rowStore is null ? document.Rows.Count : document.RowCount)) throw new InvalidOperationException("Row count mismatch.");
        if (document.Kind == "text" && !Coloured && TextView.Text != document.Text) throw new InvalidOperationException("Text view mismatch.");
        if (Coloured && new TextRange(MarkdownDisplay.Document.ContentStart, MarkdownDisplay.Document.ContentEnd).Text.Replace("\r\n", "\n").TrimEnd() != document.Text.Replace("\r\n", "\n").TrimEnd())
            throw new InvalidOperationException("Coloured code view mismatch.");
        FindBox.Text = "Hello"; Find(false);
        if (storeSearch is not null)
        {
            while (!storeSearch.Done) await Task.Delay(50);
            if (storeSearch.Error is not null) throw new InvalidOperationException(storeSearch.Error);
        }
        if (Environment.GetEnvironmentVariable("PLAINVIEWER_CAPTURE_DIR") is { Length: > 0 } captureFolder)
        {
            // Optional visual evidence for manual review, as for the web views; PLAINVIEWER_CAPTURE_ZOOM (for example 0.5) zooms first.
            if (double.TryParse(Environment.GetEnvironmentVariable("PLAINVIEWER_CAPTURE_ZOOM"), System.Globalization.CultureInfo.InvariantCulture, out double captureZoom)) ChangeZoom(captureZoom);
            UpdateLayout(); Directory.CreateDirectory(captureFolder);
            // Drawn over the theme's window colour: the Fluent window itself has a transparent backdrop.
            var layer = new DrawingVisual();
            using (var context = layer.RenderOpen())
            {
                context.DrawRectangle(IsDarkTheme() ? Brushes.Black : Brushes.White, null, new Rect(0, 0, 1100, 760));
                context.DrawRectangle(new VisualBrush(this), null, new Rect(0, 0, ActualWidth, ActualHeight));
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1100, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layer);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(captureFolder, Path.GetFileName(path) + ".png"))) png.Save(file);
            // Zooming redraws the document; return to 100% and search again for the checks below.
            if (zoom != 1) { ChangeZoom(1); UpdateLayout(); Find(false); }
        }
        if (document.Kind == "markdown" && MarkdownDisplay.Selection.Text != "Hello"
            && !MarkdownSearch.CodeBlocks(MarkdownDisplay.Document.Blocks).Any(block => ((TextBox)block.Child).SelectedText == "Hello"))
            throw new InvalidOperationException("Rendered Markdown search selected the wrong text.");
        ChangeZoom(1.2);
        if (document.Kind == "markdown") { SourceToggle.IsChecked = true; if (TextView.Text != document.Text) throw new InvalidOperationException("Source view mismatch."); }
        await ExerciseControls();
    }

    // Smoke test: the toolbar's actions on the open document, as its buttons call them (no keystrokes reach the
    // desktop; full screen is left out because it would bring the off-screen window onto the screen).
    private async Task ExerciseControls()
    {
        void Pick(ComboBox box, string item) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => (string)i.Content == item);
        if (FindBox.IsEnabled) { FindBox.Text = "Hello"; Find(false); Find(true); NextMatch(this, new RoutedEventArgs()); PreviousMatch(this, new RoutedEventArgs()); }
        ZoomIn(this, new RoutedEventArgs()); ZoomOut(this, new RoutedEventArgs()); ResetZoom(this, new RoutedEventArgs());
        foreach (string theme in new[] { "Dark", "Light", "System" }) Pick(ThemeChoice, theme);
        CancelClicked(this, new RoutedEventArgs());
        if (InWebPane && Paginated)
        {
            NextPage(this, new RoutedEventArgs()); PreviousPage(this, new RoutedEventArgs());
            FitWidth(this, new RoutedEventArgs()); FitPage(this, new RoutedEventArgs());
            // The page box: a page number and Enter go to that page; anything else explains the range.
            if (PresentationSource.FromVisual(this) is { } source)
                foreach (string entry in new[] { "1", "0" })
                {
                    PageBox.Text = entry;
                    PageBoxKeyDown(PageBox, new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                }
            if (!Status.Text.StartsWith("Enter a page number", StringComparison.Ordinal)) throw new InvalidOperationException("Page 0 was not explained.");
            if (document?.Kind is "pdf" or "word") { ThumbnailsToggle.IsChecked = true; ThumbnailsToggle.IsChecked = false; }
            await Task.Delay(300);
        }
        if (!InWebPane && document?.Kind == "markdown") { SourceToggle.IsChecked = false; if (MarkdownDisplay.Visibility != Visibility.Visible) throw new InvalidOperationException("Rendered view not shown again."); }
        if (!InWebPane && document?.Kind == "csv")
        {
            // Options: a chosen encoding and delimiter are used when the file is opened again, then automatic detection.
            Pick(EncodingChoice, "UTF-8"); Pick(DelimiterChoice, "Semicolon"); await LoadCurrent();
            if (document is null || document.Delimiter != ';' || !document.Encoding.Contains("utf-8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The chosen encoding and delimiter were not used.");
            Pick(EncodingChoice, "Auto"); Pick(DelimiterChoice, "Auto"); await LoadCurrent();
            if (document is null) throw new InvalidOperationException(Status.Text);
        }
    }
    // Timing mode for scripts/measure.ps1: shows the window off-screen, opens the file, and reports milliseconds from
    // process start until the window is drawn, and from the start of opening until the first content is drawn
    // (for PDF, Word and PowerPoint: the first page; for workbooks: the first sheet), plus peak memory in MB.
    internal async Task<Dictionary<string, object?>> MeasureAsync(string? path, DateTime processStart)
    {
        var result = new Dictionary<string, object?>();
        WindowStartupLocation = WindowStartupLocation.Manual; Left = -32000; Top = -32000; ShowActivated = false; ShowInTaskbar = false;
        var drawn = new TaskCompletionSource(); ContentRendered += (_, _) => drawn.TrySetResult();
        Show(); await drawn.Task;
        result["windowReadyMs"] = Math.Round((DateTime.Now - processStart).TotalMilliseconds);
        if (path is not null)
        {
            var firstPage = new TaskCompletionSource();
            WebPane.Rendered += () => firstPage.TrySetResult();
            var clock = Stopwatch.StartNew();
            currentPath = path; await LoadCurrent();
            if (document is null) { result["error"] = Status.Text; return result; }
            if (InWebPane) { if (await Task.WhenAny(firstPage.Task, Task.Delay(120_000)) != firstPage.Task) result["error"] = "No page was drawn within two minutes."; }
            else await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);   // after layout and drawing
            result["firstContentMs"] = Math.Round(clock.Elapsed.TotalMilliseconds);
        }
        const double MB = 1024 * 1024;
        result["appPeakMb"] = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / MB);
        result["workerPeakMb"] = Math.Round(WorkerJob.LastPeakBytes / MB);
        result["converterPeakMb"] = Math.Round(OfficeConverter.LastPeakBytes / MB);
        result["webViewPeakMb"] = Math.Round(ProcessTree.PeakBytesOfDescendants(Environment.ProcessId) / MB);
        return result;
    }
    private void OpenClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open a document", Filter = Formats.OpenDialogFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    }
    public void OpenPath(string path)
    {
        if (currentPath is not null) { var next = new MainWindow(); next.Show(); next.OpenPath(path); return; }
        currentPath = path; _ = LoadCurrent();
    }
    private async Task LoadCurrent()
    {
        if (currentPath is null) return;
        loading?.Cancel(); var operation = new CancellationTokenSource(); loading = operation;
        CancelButton.IsEnabled = true; Progress.Visibility = Visibility.Visible; Status.Text = "Opening document…";
        var stopwatch = Stopwatch.StartNew();
        string? work = null; RowStore? opened = null; var openedSheets = new Dictionary<int, RowStore>(); byte[]? webParts = null;
        try
        {
            DocumentView loaded;
            if (IsPdf(currentPath)) loaded = await LoadPdf(currentPath, operation.Token);
            else if (IsOffice(currentPath)) loaded = await LoadOffice(currentPath, operation.Token);
            else if (IsPicture(currentPath)) loaded = await LoadPicture(currentPath, operation.Token);
            else
            {
                work = OfficeConverter.NewWorkFolder();
                string folder = work, opening = currentPath;
                loaded = await WithPassword(password => WorkerClient.Load(opening, Choice(EncodingChoice), Choice(DelimiterChoice), folder, operation.Token, password));
                // Store names and counts come from the worker, so only the names it may use are accepted, and each
                // store must hold exactly the rows the worker reported.
                // Web pages and books: the parts the worker collected, for the page to clean and show (Assets/web).
                if (loaded.Kind == "web") webParts = loaded.Store == WebDocuments.Output && new FileInfo(Path.Combine(work, WebDocuments.Output)) is { Exists: true, Length: <= 256L * 1024 * 1024 } parts
                    ? File.ReadAllBytes(parts.FullName) : throw new InvalidDataException("Unexpected worker output.");
                else if (loaded.Store.Length > 0) opened = loaded.Store == "rows" ? RowStore.Open(work, "rows") : throw new InvalidDataException("Unexpected worker output.");
                if (opened is not null && opened.Count != loaded.RowCount) throw new InvalidDataException("Unexpected worker output.");
                for (int i = 0; i < loaded.Sheets.Count; i++)
                {
                    if (loaded.Sheets[i].Store is not { Length: > 0 } name) continue;
                    var sheetStore = openedSheets[i] = name == $"sheet{i}" ? RowStore.Open(work, name) : throw new InvalidDataException("Unexpected worker output.");
                    if (sheetStore.Count != loaded.Sheets[i].RowCount) throw new InvalidDataException("Unexpected worker output.");
                }
                // Pictures on sheets: files the worker wrote to the work folder, under names only it may use.
                for (int i = 0; i < loaded.Sheets.Count; i++)
                    foreach (var picture in loaded.Sheets[i].Pictures.Where(p => p.Media.Length > 0))
                        if (ImageFiles.ContentTypeOf(picture.Media) is null || !picture.Media.StartsWith($"media-{i}-", StringComparison.Ordinal) || !File.Exists(Path.Combine(work, picture.Media)))
                            throw new InvalidDataException("Unexpected worker output.");
            }
            if (loading != operation) return;
            // Pictures on sheets or in web pages and books stay in the work folder while the document is open.
            bool media = loaded.Sheets.Any(s => s.Pictures.Any(p => p.Media.Length > 0)) || webParts is not null;
            bool stores = opened is not null || openedSheets.Count > 0 || media;
            ReplaceRowStore(opened, openedSheets, stores ? work : null);
            if (stores) { opened = null; openedSheets = []; work = null; }
            if (loaded.Kind == "sheet") await LoadSheets(loaded, operation.Token);
            else if (webParts is not null) await LoadWeb(webParts, operation.Token);
            if (loading != operation) return;
            document = loaded; Title = Path.GetFileName(currentPath) + " · Plain Viewer";
            openSeconds = stopwatch.Elapsed.TotalSeconds;
            string size = rowStore is null ? "" : $"{document.RowCount:N0} {(document.Kind == "lines" ? "lines" : "rows")} · ";
            Display(); Status.Text = $"Read only · {document.Encoding} · {size}Opened in {openSeconds:F2}s. {document.Notice}";
            if (InWebPane) ShowWebStatus();
        }
        catch (OperationCanceledException) { if (loading == operation) Status.Text = "Opening cancelled. Choose a file to try again."; }
        catch (Exception ex)
        {
            if (loading != operation) return;
            unexpected = ex is DocumentException || DiskSpace.IsFull(ex) ? null : ex;
            Status.Text = ex is DocumentException ? ex.Message : DiskSpace.IsFull(ex) ? DiskSpace.Message : UnexpectedError;
        }
        finally
        {
            opened?.Dispose(); foreach (var store in openedSheets.Values) store.Dispose();
            if (work is not null) OfficeConverter.Delete(work);
            if (loading == operation) { CancelButton.IsEnabled = false; Progress.Visibility = Visibility.Collapsed; }
            operation.Dispose(); if (loading == operation) loading = null;
        }
    }
    private void ReplaceRowStore(RowStore? store, Dictionary<int, RowStore>? sheets, string? folder)
    {
        storeSearch?.Dispose(); storeSearch = null; storeMatch = -1;
        CsvGrid.ItemsSource = null;
        var previous = sheetStores; sheetStores = sheets ?? [];
        WebPane.Data = sheetStores.Count > 0 || folder is not null ? SheetRequest : null;
        rowStore?.Dispose(); foreach (var old in previous.Values) old.Dispose();
        if (rowStoreFolder is not null) OfficeConverter.Delete(rowStoreFolder);
        rowStore = store; rowStoreFolder = folder;
    }

    // Answers the spreadsheet page's requests for rows and searches of large sheets. Runs on a background thread.
    private byte[]? SheetRequest(string path, System.Collections.Specialized.NameValueCollection query)
    {
        if (path == "/media")
        {
            // Only a picture the worker wrote, and only if its bytes are still the kind of picture its name says.
            string name = query["name"] ?? "";
            if (rowStoreFolder is not { } folder) return null;
            // A font the worker saved from a book: only under a font name, and only if its bytes are that kind of font.
            if (WebDocuments.FontContentType(name) is { } fontType)
            {
                var fontFile = new FileInfo(Path.Combine(folder, name));
                if (!fontFile.Exists || fontFile.Length > 16L * 1024 * 1024) return null;
                byte[] font = File.ReadAllBytes(fontFile.FullName);
                return "font/" + WebDocuments.FontType(font) == fontType ? font : null;
            }
            if (ImageFiles.ContentTypeOf(name) is not { } type) return null;
            var file = new FileInfo(Path.Combine(folder, name));
            if (!file.Exists || file.Length > 20L * 1024 * 1024) return null;
            byte[] bytes = File.ReadAllBytes(file.FullName);
            return ImageFiles.Identify(bytes)?.ContentType == type ? bytes : null;
        }
        if (!int.TryParse(query["sheet"], out int sheet) || !sheetStores.TryGetValue(sheet, out var store)) return null;
        if (path == "/rows")
        {
            int start = int.Parse(query["start"] ?? ""), count = int.Parse(query["count"] ?? "");
            if (count is < 0 or > 2000 || start < 0 || start > store.Count - count) return null;
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { start, rows = store.Read(start, count) });
        }
        string needle = query["q"] ?? "";
        if (needle.Length is 0 or > 1000) return null;
        var hits = new List<int[]>(); int total = 0;
        for (int start = 0; start < store.Count; start += 4096)
        {
            var rows = store.Read(start, Math.Min(4096, store.Count - start));
            for (int i = 0; i < rows.Length; i++)
                for (int c = 1; c < rows[i].Length; c++)                         // field 0 is the row's alignment
                    if (rows[i][c].Contains(needle, StringComparison.OrdinalIgnoreCase)) { total++; if (hits.Count < 10_000) hits.Add([start + i, c - 1]); }
        }
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { total, hits });
    }
    private void Display()
    {
        if (document is null) return;
        Welcome.Visibility = Visibility.Collapsed; TextView.Visibility = MarkdownDisplay.Visibility = CsvGrid.Visibility = Visibility.Collapsed;
        SourceToggle.Visibility = document.Kind == "markdown" ? Visibility.Visible : Visibility.Collapsed;
        bool web = InWebPane;
        WebPane.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
        bool picture = document.Kind == "image";
        PageControls.Visibility = Paginated || picture || Book ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in new FrameworkElement[] { PreviousPageButton, PageLabel, PageBox, PageCount, NextPageButton })
            element.Visibility = picture ? Visibility.Collapsed : Visibility.Visible;
        // Books and web archives reflow their text, so there is no page to fit.
        FitWidthButton.Visibility = FitPageButton.Visibility = Book ? Visibility.Collapsed : Visibility.Visible;
        RotateLeftButton.Visibility = RotateRightButton.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
        // Pictures (and TIFF scans shown as pages) have no text: search is switched off and says why.
        bool noText = picture || document.Encoding == "TIFF picture";
        string? noSearch = noText ? "Pictures have no text to search." : null;
        foreach (var control in new Control[] { FindBox, PreviousButton, NextButton })
        {
            control.IsEnabled = !noText; control.ToolTip = noSearch;
            ToolTipService.SetShowOnDisabled(control, true);
            System.Windows.Automation.AutomationProperties.SetHelpText(control, noSearch ?? "");
        }
        PageLabel.Text = document.Kind == "slides" ? "Slide" : Parts ? "Part" : "Page";
        System.Windows.Automation.AutomationProperties.SetName(PageBox, document.Kind == "slides" ? "Go to slide number" : Parts ? "Go to part number" : "Go to page number");
        // Text encoding and CSV delimiter appear only where they apply (Zain's choice, DECISIONS.md D14): the encoding for
        // text, CSV, Markdown and data files, the delimiter for CSV.
        EncodingChoice.Visibility = web ? Visibility.Collapsed : Visibility.Visible;
        DelimiterChoice.Visibility = document.Kind == "csv" ? Visibility.Visible : Visibility.Collapsed;
        // Page thumbnails beside PDF and Word documents, on request (slides always have their strip).
        bool documentPages = document.Kind is "pdf" or "word";
        ThumbnailsToggle.Visibility = documentPages ? Visibility.Visible : Visibility.Collapsed;
        if (documentPages && ThumbnailsToggle.IsChecked == true) WebPane.ShowThumbnails(true);
        if (web) { lastQuery = ""; matchIndex = -1; WebPane.FocusDocument(); return; }
        if (document.Kind is "csv" or "lines")
        {
            // Large plain text shows one line per row, with line numbers as row headers.
            bool lines = document.Kind == "lines";
            CsvGrid.Columns.Clear();
            int count = rowStore is not null ? document.Columns : document.Rows.Count == 0 ? 0 : document.Rows.Max(row => row.Length);
            for (int i = 0; i < count; i++)
                CsvGrid.Columns.Add(new DataGridTextColumn { Header = lines ? "Text" : ColumnName(i), Binding = new Binding($"[{i}]") { FallbackValue = "" }, Width = lines ? DataGridLength.Auto : 140 });
            CsvGrid.HeadersVisibility = lines ? DataGridHeadersVisibility.Row : DataGridHeadersVisibility.All;
            if (lines) CsvGrid.FontFamily = new FontFamily("Consolas"); else CsvGrid.ClearValue(FontFamilyProperty);
            System.Windows.Automation.AutomationProperties.SetName(CsvGrid, lines ? "Read-only text, one line per row" : "Read-only CSV grid");
            CsvGrid.ItemsSource = rowStore is not null ? new StoreRows(rowStore)
                : document.Rows.Select(row => Enumerable.Range(0, count).Select(i => i < row.Length ? row[i] : "").ToArray()).ToList();
            CsvGrid.Visibility = Visibility.Visible;
        }
        else if (document.Kind == "markdown" && SourceToggle.IsChecked != true)
        {
            // Refill the existing document rather than replacing it: a screen reader that queried the window while the
            // file was loading (as Narrator does) keeps reading the original document object, which would stay empty.
            var flow = MarkdownDisplay.Document;
            flow.Blocks.Clear(); flow.PagePadding = new Thickness(18); flow.FontFamily = new FontFamily("Segoe UI"); flow.FontSize = 16 * zoom;
            foreach (var block in document.Blocks) flow.Blocks.Add(MarkdownRenderer.Render(block, zoom, OpenLink));
            MarkdownRenderer.ResizeCode(flow, MarkdownDisplay.ActualWidth);
            MarkdownDisplay.Visibility = Visibility.Visible;
        }
        else if (Coloured) ShowColouredCode();
        else { TextView.Text = document.Text; TextView.Visibility = Visibility.Visible; }
        lastQuery = ""; matchIndex = -1; ApplyZoom();
    }
    // Code, project and data files with syntax colours, shown read-only in the rich text view (search, copy and zoom
    // work as for Markdown). High contrast keeps the system's own colours, so the plain text view is used there.
    private bool Coloured => document is { Kind: "text", Spans.Count: > 0 } && !SystemParameters.HighContrast;
    private void ShowColouredCode()
    {
        bool dark = IsDarkTheme();
        Brush Colour(int kind) => (kind, dark) switch
        {
            (CodeHighlighter.Comment, false) => new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00)), (CodeHighlighter.Comment, true) => new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55)),
            (CodeHighlighter.String, false) => new SolidColorBrush(Color.FromRgb(0xA3, 0x15, 0x15)), (CodeHighlighter.String, true) => new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78)),
            (CodeHighlighter.Keyword, false) => new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF)), (CodeHighlighter.Keyword, true) => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
            (CodeHighlighter.Number, false) => new SolidColorBrush(Color.FromRgb(0x09, 0x86, 0x58)), (CodeHighlighter.Number, true) => new SolidColorBrush(Color.FromRgb(0xB5, 0xCE, 0xA8)),
            (CodeHighlighter.Markup, false) => new SolidColorBrush(Color.FromRgb(0x80, 0x00, 0x00)), (CodeHighlighter.Markup, true) => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
            (_, false) => new SolidColorBrush(Color.FromRgb(0x81, 0x1F, 0x3F)), (_, true) => new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
        };
        string text = document!.Text;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        void Add(int start, int end, int kind)
        {
            for (int at = start; at < end;)
            {
                int line = text.IndexOf('\n', at, end - at);
                int stop = line < 0 ? end : line;
                if (stop > at)
                {
                    var run = new Run(text[at..stop].TrimEnd('\r'));
                    if (kind > 0) { run.Foreground = Colour(kind); if (kind == CodeHighlighter.Comment) run.FontStyle = FontStyles.Italic; }
                    paragraph.Inlines.Add(run);
                }
                if (line < 0) break;
                paragraph.Inlines.Add(new LineBreak()); at = line + 1;
            }
        }
        int position = 0;
        var spans = document.Spans;
        for (int i = 0; i + 2 < spans.Count; i += 3)
        {
            int start = spans[i], length = spans[i + 1];
            if (start < position || start + length > text.Length) continue;
            Add(position, start, 0); Add(start, start + length, spans[i + 2]); position = start + length;
        }
        Add(position, text.Length, 0);
        var flow = MarkdownDisplay.Document;
        flow.Blocks.Clear(); flow.PagePadding = new Thickness(12); flow.FontFamily = new FontFamily("Cascadia Mono, Consolas"); flow.FontSize = 15 * zoom;
        // No wrapping, like the plain text view: the page is as wide as the longest line, and the view scrolls sideways.
        int longest = text.Split('\n').Max(l => l.Length);
        flow.PageWidth = Math.Max(400, longest * 9.5 * zoom + 48);
        flow.Blocks.Add(paragraph);
        MarkdownDisplay.Visibility = Visibility.Visible;
    }
    private void OpenLink(string address)
    {
        if (LinkPolicy.LaunchAddress(address) is not { } launch) { Status.Text = "This link was not opened because its address is not valid."; return; }
        if (MessageBox.Show(this, "Open this address in your default application?\n\n" + launch, "Open link", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true }); }
        catch { Status.Text = "The link could not open. Check your default browser or email application."; }
    }
    private void Find(bool previous)
    {
        if (document is null || FindBox.Text.Length == 0 || !FindBox.IsEnabled) return;
        if (InWebPane) { Status.Text = "Searching…"; WebPane.Find(FindBox.Text, previous); lastQuery = FindBox.Text; return; }
        if (rowStore is not null) { FindInStore(FindBox.Text, previous); return; }
        string query = FindBox.Text; var hits = new List<int>();
        if (MarkdownDisplay.Visibility == Visibility.Visible)
        {
            var matches = MarkdownSearch.Find(MarkdownDisplay, query);
            if (lastQuery != query) matchIndex = previous ? 0 : -1;
            if (matches.Count > 0)
            {
                matchIndex = (matchIndex + (previous ? -1 : 1) + matches.Count) % matches.Count;
                matches[matchIndex].Select();
            }
            Status.Text = matches.Count == 0 ? "No matches." : $"Match {matchIndex + 1} of {matches.Count}.";
            lastQuery = query; return;
        }
        if (document.Kind == "csv")
        {
            var rows = (List<string[]>)CsvGrid.ItemsSource;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Any(cell => cell.Contains(query, StringComparison.OrdinalIgnoreCase))) hits.Add(i);
            if (lastQuery != query) matchIndex = previous ? 0 : -1;
            if (hits.Count > 0) { matchIndex = (matchIndex + (previous ? -1 : 1) + hits.Count) % hits.Count; CsvGrid.SelectedItem = rows[hits[matchIndex]]; CsvGrid.ScrollIntoView(CsvGrid.SelectedItem); }
            Status.Text = hits.Count == 0 ? "No matches in loaded rows." : $"Matching row {matchIndex + 1} of {hits.Count} in loaded rows. {document.Notice}";
        }
        else
        {
            string text = TextView.Text;
            for (int start = 0; start <= text.Length - query.Length;)
            { int found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase); if (found < 0) break; hits.Add(found); start = found + query.Length; }
            if (lastQuery != query) matchIndex = previous ? 0 : -1;
            if (hits.Count > 0)
            {
                matchIndex = (matchIndex + (previous ? -1 : 1) + hits.Count) % hits.Count;
                TextView.Focus(); TextView.Select(hits[matchIndex], query.Length); TextView.ScrollToLine(TextView.GetLineIndexFromCharacterIndex(hits[matchIndex]));
            }
            Status.Text = hits.Count == 0 ? "No matches." : $"Match {matchIndex + 1} of {hits.Count}.";
        }
        lastQuery = query;
    }
    // CSV and large text search on a background thread: the first match shows as soon as it is found, progress
    // appears in the status line, and Cancel or a new search stops it.
    private void FindInStore(string query, bool previous)
    {
        if (storeSearch is null || storeSearch.Query != query || storeSearch.Error is not null)
        {
            storeSearch?.Dispose(); storeMatch = -1; lastQuery = query;
            storeSearch = new StoreSearch(rowStore!, query, search => Dispatcher.InvokeAsync(() => SearchChanged(search, previous)));
            CancelButton.IsEnabled = true; Status.Text = "Searching…";
            return;
        }
        ShowStoreMatch(previous);
    }
    private void SearchChanged(StoreSearch search, bool previous)
    {
        if (search != storeSearch) return;
        if (storeMatch < 0 && search.Count > 0) ShowStoreMatch(previous); else ShowSearchStatus();
        if (search.Done && loading is null) CancelButton.IsEnabled = false;
    }
    private void ShowStoreMatch(bool previous)
    {
        var search = storeSearch!; int count = search.Count;
        if (count > 0 && CsvGrid.ItemsSource is StoreRows rows)
        {
            storeMatch = storeMatch < 0 ? (previous && search.Done ? count - 1 : 0) : (storeMatch + (previous ? -1 : 1) + count) % count;
            var row = rows.Row(search.Hit(storeMatch));
            CsvGrid.SelectedItem = row; CsvGrid.ScrollIntoView(row);
        }
        ShowSearchStatus();
    }
    private void ShowSearchStatus()
    {
        var search = storeSearch!; int count = search.Count; string unit = document?.Kind == "lines" ? "line" : "row";
        Status.Text = search.Error ?? (search.Done
            ? count == 0 ? "No matches." : $"Matching {unit} {storeMatch + 1} of {count:N0}.{(search.Truncated ? " The search stopped at 1,000,000 matches." : "")}"
            : $"Searching… {search.Progress:P0} · {count:N0} matching {unit}s so far{(storeMatch >= 0 ? $", showing {storeMatch + 1}" : "")}. Cancel stops the search.");
    }
    private static string ColumnName(int index) { string name = ""; for (int value = index + 1; value > 0; value = (value - 1) / 26) name = (char)('A' + (value - 1) % 26) + name; return name; }
    private void ApplyZoom() { TextView.FontSize = 16 * zoom; CsvGrid.FontSize = 14 * zoom; MarkdownDisplay.FontSize = 16 * zoom; if (document?.Kind == "markdown") MarkdownDisplay.Document.FontSize = 16 * zoom; ZoomButton.Content = $"{zoom:P0}"; }
    private void ChangeZoom(double value) { zoom = Math.Clamp(value, 0.5, 3); if (document?.Kind == "markdown" || Coloured) Display(); else ApplyZoom(); }
    private void ZoomBy(int direction)
    {
        if (InWebPane) { WebPane.Zoom(direction > 0 ? "in" : direction < 0 ? "out" : 1.0); return; }
        ChangeZoom(direction == 0 ? 1 : zoom + 0.1 * direction);
    }
    private void ZoomIn(object s, RoutedEventArgs e) => ZoomBy(1);
    private void ZoomOut(object s, RoutedEventArgs e) => ZoomBy(-1);
    private void ResetZoom(object s, RoutedEventArgs e) => ZoomBy(0);
    private void FitWidth(object s, RoutedEventArgs e) => WebPane.Zoom("page-width");
    private void FitPage(object s, RoutedEventArgs e) => WebPane.Zoom("page-fit");
    private void PreviousPage(object s, RoutedEventArgs e) => WebPane.Step(-1);
    private void NextPage(object s, RoutedEventArgs e) => WebPane.Step(1);
    private async Task<DocumentView> LoadOffice(string path, CancellationToken cancellation)
    {
        var (prepared, pdf) = await WithPassword(password => OfficeConverter.Convert(path, cancellation, () => Status.Text = "Preparing the document for viewing…", password));
        Welcome.Visibility = TextView.Visibility = MarkdownDisplay.Visibility = CsvGrid.Visibility = Visibility.Collapsed;
        WebPane.Visibility = Visibility.Visible;
        bool slides = prepared.Kind == "slides";
        await WebPane.LoadPdf(pdf, IsDarkTheme(), cancellation, slides);
        if (slides) prepared.Notice = (prepared.Notice + " Slides are shown as still pictures: animations, transitions, audio and video do not play.").Trim();
        return prepared;
    }
    private void PageBoxKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(PageBox.Text.Trim(), out int page) && page >= 1 && page <= WebPane.Pages) { WebPane.GoToPage(page); WebPane.FocusDocument(); }
        else Status.Text = $"Enter a page number from 1 to {WebPane.Pages}.";
        e.Handled = true;
    }
    private async Task<DocumentView> LoadPicture(string path, CancellationToken cancellation)
    {
        var picture = await Task.Run(() => ImageFiles.Snapshot(path), cancellation);
        string notice = "";
        if (picture.Format == "HEIF")
        {
            // HEIC photos: the worker decodes them with Windows' own codec and writes a PNG to a work folder.
            string work = OfficeConverter.NewWorkFolder();
            try
            {
                var converted = await WorkerClient.Load(path, "Auto", "Auto", work, cancellation);
                var file = new FileInfo(Path.Combine(work, "picture.png"));
                if (converted.Kind != "image" || converted.Store != "picture.png" || !file.Exists || file.Length > 1024L * 1024 * 1024) throw new InvalidDataException("Unexpected worker output.");
                picture = ImageFiles.Identify(await File.ReadAllBytesAsync(file.FullName, cancellation)) is { Format: "PNG" } png ? png : throw new InvalidDataException("Unexpected worker output.");
                notice = converted.Notice;
            }
            finally { OfficeConverter.Delete(work); }
        }
        Welcome.Visibility = TextView.Visibility = MarkdownDisplay.Visibility = CsvGrid.Visibility = Visibility.Collapsed;
        WebPane.Visibility = Visibility.Visible;
        await WebPane.LoadPicture(picture, IsDarkTheme(), cancellation);
        var view = new DocumentView
        {
            Kind = "image", Encoding = picture.Format + " picture",
            Notice = picture.Incomplete ? "This picture file is incomplete (cut short, for example by an interrupted download), so part of it may be missing. Get a complete copy to see all of it."
                : picture.Format == "SVG" ? "SVG pictures are shown as still images: any scripts or linked content in them never run or load." : notice
        };
        if (notice.Length > 0) view.Encoding = "HEIC picture";
        return view;
    }
    private void ThumbnailsChanged(object s, RoutedEventArgs e)
    {
        bool show = ThumbnailsToggle.IsChecked == true;
        if (document?.Kind is "pdf" or "word") WebPane.ShowThumbnails(show);
        if (AppSettings.Enabled && AppSettings.Current.Thumbnails != show) { AppSettings.Current.Thumbnails = show; AppSettings.Save(); }
    }
    private void RotateLeft(object s, RoutedEventArgs e) => WebPane.Rotate(-1);
    private void RotateRight(object s, RoutedEventArgs e) => WebPane.Rotate(1);
    private async Task<DocumentView> LoadPdf(string path, CancellationToken cancellation)
    {
        var data = await Task.Run(() => PdfFiles.Snapshot(path), cancellation);
        // Show the PDF area before loading so PDF.js can measure the page width.
        Welcome.Visibility = TextView.Visibility = MarkdownDisplay.Visibility = CsvGrid.Visibility = Visibility.Collapsed;
        WebPane.Visibility = Visibility.Visible;
        await WebPane.LoadPdf(data, IsDarkTheme(), cancellation);
        return new DocumentView { Kind = "pdf", Encoding = "PDF" };
    }
    private async Task LoadSheets(DocumentView view, CancellationToken cancellation)
    {
        // The worker has already turned the workbook into display text; the page only lays it out.
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { sheets = view.Sheets, styles = view.CellStyles },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Welcome.Visibility = TextView.Visibility = MarkdownDisplay.Visibility = CsvGrid.Visibility = Visibility.Collapsed;
        WebPane.Visibility = Visibility.Visible;
        await WebPane.LoadSheets(json, IsDarkTheme(), cancellation);
    }
    private async Task LoadWeb(byte[] json, CancellationToken cancellation)
    {
        Welcome.Visibility = TextView.Visibility = MarkdownDisplay.Visibility = CsvGrid.Visibility = Visibility.Collapsed;
        WebPane.Visibility = Visibility.Visible;
        await WebPane.LoadWeb(json, IsDarkTheme(), cancellation);
    }
    // EPUB books are shown as pages (after 0.9.0); web archives with several parts move by part. Neither has a page to fit.
    private bool EpubBook => document?.Kind == "web" && document.Encoding == "EPUB book";
    private bool Parts => document?.Kind == "web" && !EpubBook && WebPane.Pages > 1;
    private bool Book => EpubBook || Parts;
    private void ShowWebStatus()
    {
        if (document is null || !InWebPane) return;
        ZoomButton.Content = $"{WebPane.Scale:P0}";
        if (document.Kind == "image")
        {
            string turn = WebPane.Rotation == 0 ? "" : $" · Turned {WebPane.Rotation}°";
            string size = document.Encoding.StartsWith("SVG") ? "scalable drawing" : $"{WebPane.PictureWidth:N0} × {WebPane.PictureHeight:N0} pixels";
            Status.Text = $"Read only · {document.Encoding} · {size}{turn} · Opened in {openSeconds:F2}s. {document.Notice}";
        }
        else if (Paginated)
        {
            string unit = document.Kind == "slides" ? "Slide" : "Page";
            string type = document.Encoding;   // "Word document", "OpenDocument text", "TIFF picture", "PDF"…
            PageCount.Text = $"of {WebPane.Pages}";
            if (!PageBox.IsKeyboardFocused) PageBox.Text = WebPane.Page.ToString();
            Status.Text = $"Read only · {type} · {unit} {WebPane.Page} of {WebPane.Pages} · Opened in {openSeconds:F2}s. {document.Notice}";
        }
        else if (document.Kind == "web")
        {
            PageCount.Text = $"of {WebPane.Pages}";
            if (!PageBox.IsKeyboardFocused) PageBox.Text = WebPane.Page.ToString();
            string where = EpubBook ? $" · Page {WebPane.Page} of {WebPane.Pages}" + (WebPane.Chapters > 1 ? $" · Chapter {WebPane.Chapter} of {WebPane.Chapters}" : "")
                : Parts ? $" · Part {WebPane.Page} of {WebPane.Pages}" : "";
            Status.Text = $"Read only · {document.Encoding}{where} · Opened in {openSeconds:F2}s. {document.Notice} {WebPane.PageNotice}".TrimEnd();
        }
        else Status.Text = $"Read only · {document.Encoding} · Sheet {WebPane.Page} of {WebPane.Pages}: {WebPane.SheetName} · Opened in {openSeconds:F2}s. {document.Notice}";
    }
    private async Task<(int Current, int Total)> PdfFind(string query)
    {
        var result = new TaskCompletionSource<(int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(int current, int total, bool finished) { if (finished) result.TrySetResult((current, total)); }
        WebPane.FindResult += Handler;
        try { WebPane.Find(query, false); return await result.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { WebPane.FindResult -= Handler; }
    }
    // Smoke test only (App.xaml.cs): no dialog is shown; the password given for the file, or none, is used instead.
    internal static bool TestMode { get; set; }
    internal static string? TestPassword { get; set; }

    // Word, Excel and PowerPoint files protected with a password: ask, open again with it, and ask again while it is wrong.
    private async Task<T> WithPassword<T>(Func<string?, Task<T>> open)
    {
        string? password = null;
        while (true)
        {
            try { return await open(password); }
            catch (PasswordException ex)
            {
                Status.Text = ex.Message;
                password = AskPassword(ex.Incorrect, ex.Incorrect ? ex.Message : ex.Message.Replace(" Enter its password to view it.", "") + " Enter its password to view it.");
                if (password is null) throw new DocumentException("This file is protected with a password. Open it again and enter its password to view it.");
            }
        }
    }
    private string? AskPdfPassword(bool incorrect) =>
        AskPassword(incorrect, incorrect ? "That password is not correct. Try again." : "This PDF is protected. Enter its password to view it.");
    private string? AskPassword(bool incorrect, string prompt)
    {
        if (TestMode) return incorrect ? null : TestPassword;
        var box = new PasswordBox { Margin = new Thickness(0, 10, 0, 14), MinWidth = 280 };
        System.Windows.Automation.AutomationProperties.SetName(box, "Password");
        var ok = new Button { Content = "Open", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6, 16, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 });
        panel.Children.Add(box); panel.Children.Add(buttons);
        var dialog = new Window { Title = "Password required", Owner = IsVisible ? this : null, Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        // The password goes straight to PDF.js or the worker; it is never stored or logged.
        return dialog.ShowDialog() == true ? box.Password : null;
    }
    private bool IsDarkTheme()
    {
        string choice = ThemeChoice.SelectedItem is ComboBoxItem ? Choice(ThemeChoice) : "System";
        if (choice != "System") return choice == "Dark";
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
    private void NextMatch(object s, RoutedEventArgs e) => Find(false);
    private void PreviousMatch(object s, RoutedEventArgs e) => Find(true);
    private void FindKeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) Find(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
    private void ViewChanged(object s, RoutedEventArgs e) => Display();
    private void OptionsChanged(object s, SelectionChangedEventArgs e) { if (IsLoaded && currentPath is not null && !InWebPane) _ = LoadCurrent(); }
    private void CancelClicked(object s, RoutedEventArgs e)
    {
        loading?.Cancel();
        if (storeSearch is { Done: false }) { storeSearch.Dispose(); storeSearch = null; storeMatch = -1; Status.Text = "Search cancelled."; CancelButton.IsEnabled = loading is not null; }
    }
    // WPF still marks runtime Fluent theme switching experimental in this SDK.
#pragma warning disable WPF0001
    private void ThemeChanged(object s, SelectionChangedEventArgs e)
    {
        if (ThemeChoice is null) return;
        string choice = Choice(ThemeChoice);
        ThemeMode = choice switch { "Dark" => ThemeMode.Dark, "Light" => ThemeMode.Light, _ => ThemeMode.System };
        if (InWebPane) WebPane.SetTheme(IsDarkTheme());
        if (AppSettings.Enabled && AppSettings.Current.Theme != choice) { AppSettings.Current.Theme = choice; AppSettings.Save(); }
    }
#pragma warning restore WPF0001
    // The last theme choice and window placement. A new window opened while others are showing is offset so it does not
    // hide them exactly.
    private void RestoreSettings()
    {
        var settings = AppSettings.Current;
        ThemeChoice.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ThumbnailsToggle.IsChecked = settings.Thumbnails;
        if (settings.Bounds(MinWidth, MinHeight) is { } bounds)
        {
            int others = Application.Current.Windows.OfType<MainWindow>().Count(w => w != this && w.IsVisible);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left + 32 * others; Top = bounds.Top + 32 * others;
            Width = bounds.Width; Height = bounds.Height;
        }
        if (settings.Maximized) WindowState = WindowState.Maximized;
    }
    private void SavePlacement()
    {
        bool fullScreen = WindowStyle == WindowStyle.None;   // F11: remember the state from before full screen
        var bounds = WindowState == WindowState.Normal && !fullScreen ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        var settings = AppSettings.Current;
        if (!bounds.IsEmpty) { settings.Left = bounds.Left; settings.Top = bounds.Top; settings.Width = bounds.Width; settings.Height = bounds.Height; }
        settings.Maximized = fullScreen ? savedState == WindowState.Maximized : WindowState == WindowState.Maximized;
        AppSettings.Save();
    }
    private void NumberRow(object s, DataGridRowEventArgs e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    private void FileDropped(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) foreach (var file in files) OpenPath(file); }
    private static string Version => typeof(MainWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
    // The build date recorded by PlainViewer.App.csproj, in the PC's local time.
    internal static string BuiltOn() =>
        typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value is { } value && DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var built)
            ? "\nBuilt " + built.ToLocalTime().ToString("d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture) : "";
    private void AboutClicked(object s, RoutedEventArgs e) => MessageBox.Show(this, $"Plain Viewer {Version} (beta){BuiltOn()}\n\nRead-only viewer for PDF, Word, Excel, PowerPoint, OpenDocument, RTF, pictures, text, CSV, Markdown and data files. Nothing is uploaded and no network is used. MIT licence.\n\nPDF uses PDF.js (Apache-2.0) inside Microsoft Edge WebView2. Word, PowerPoint, OpenDocument text and presentations, RTF and TIFF are converted to PDF by LibreOffice (MPL-2.0). Excel number formats use ExcelNumberFormat (MIT). Markdown uses Markdig (BSD-2-Clause). See THIRD-PARTY-NOTICES.md.\n\n{SafetyNote()}", "About Plain Viewer");

    private static string SafetyNote() =>
        "Files are read by separate processes that run at low integrity with memory and time limits: they cannot change your files or other programs. PDF pages are drawn inside WebView2's own sandbox.\n\n" +
        (NetworkBlock.IsOn()
            ? "Network block: on. Windows Firewall blocks the document reader and converter from the network."
            : "Network block: off. To turn it on, run the Plain Viewer installer again and allow the administrator prompt.");
    private void WindowKeyDown(object s, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.O) OpenClicked(s, e);
        else if (ctrl && e.Key == Key.F) FindBox.Focus();
        else if (ctrl && e.Key == Key.W) Close();
        else if (ctrl && (e.Key == Key.Add || e.Key == Key.OemPlus)) ZoomBy(1);
        else if (ctrl && (e.Key == Key.Subtract || e.Key == Key.OemMinus)) ZoomBy(-1);
        else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0)) ZoomBy(0);
        else if (e.Key == Key.F3) Find(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        else if (ctrl && e.Key is Key.PageUp or Key.PageDown && document?.Kind == "sheet") WebPane.ChangeSheet(e.Key == Key.PageDown ? 1 : -1);
        else if (ctrl && e.Key is Key.PageUp or Key.PageDown && EpubBook) WebPane.ChangeChapter(e.Key == Key.PageDown ? 1 : -1);
        else if (ctrl && e.Key == Key.R && document?.Kind == "image") WebPane.Rotate(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        else if (e.Key == Key.F11) { if (WindowStyle == WindowStyle.None) { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = savedState; } else { savedState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; } }
        else return;
        e.Handled = true;
    }
}
