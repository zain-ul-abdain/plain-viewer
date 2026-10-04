using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PlainViewer.Core;
namespace PlainViewer.App;

// A locked-down WebView2 that shows one document page: the PDF viewer (PDF.js parses the PDF inside WebView2's
// sandboxed renderer) or the spreadsheet grid (display text prepared by the worker). This class only supplies
// the document bytes and relays messages. Every other request is refused and counted.
internal sealed class DocumentWebView : Border
{
    private const string AppHost = "app.plainviewer.invalid";
    private const string DocumentHost = "doc.plainviewer.invalid";
    private static Task<CoreWebView2Environment>? environment;
    private readonly WebView2 web = new();
    private byte[]? bytes;
    private string resource = "";
    private string contentType = "";
    private string page = "";
    private TaskCompletionSource<int>? opening;

    public int Page { get; private set; }         // PDF page, or 1-based sheet index
    public int Pages { get; private set; }        // PDF page count, or sheet count
    public string SheetName { get; private set; } = "";
    public int PictureWidth { get; private set; }  // pixels, for pictures
    public int PictureHeight { get; private set; }
    public int Rotation { get; private set; }      // degrees clockwise, for pictures
    public double Scale { get; private set; } = 1;
    public int BlockedRequests { get; private set; }
    public string PageNotice { get; private set; } = "";   // web pages: what the page removed before showing the document
    public event Action? StateChanged;
    public event Action<int, int, bool>? FindResult;          // current, total, finished
    public event Action<string>? LinkRequested;
    public event Action? Rendered;                             // first page or sheet drawn
    public Func<bool, string?>? AskPassword;                  // argument: previous password was wrong

    public DocumentWebView()
    {
        Child = web;
        AutomationProperties.SetName(this, "Document");
        AutomationProperties.SetName(web, "Document content");
    }

    private static Task<CoreWebView2Environment> SharedEnvironment() => environment ??= CreateEnvironment();

    private static Task<CoreWebView2Environment> CreateEnvironment()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainViewer", "WebView2");
        // Background services off; crash reports stay local. Document requests are blocked separately below.
        var options = new CoreWebView2EnvironmentOptions(
            "--disable-background-networking --disable-component-update --disable-domain-reliability --disable-sync --no-pings")
        { IsCustomCrashReportingEnabled = true };
        return CoreWebView2Environment.CreateAsync(null, data, options);
    }

    private async Task EnsureReady()
    {
        if (web.CoreWebView2 is not null) return;
        try { await web.EnsureCoreWebView2Async(await SharedEnvironment()); }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new DocumentException("PDF, Word, Excel, PowerPoint and picture files need the Microsoft Edge WebView2 Runtime, which is not installed on this PC. " +
                "Text, CSV, Markdown and data files still open. Run the Plain Viewer installer again (it includes the WebView2 Runtime), then open the file again.");
        }
        var core = web.CoreWebView2 ?? throw new DocumentException("The document view could not start. Check that Microsoft Edge WebView2 Runtime is installed.");
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsReputationCheckingRequired = false;
        settings.IsWebMessageEnabled = true;

        core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Deny);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{AppHost}/{page}", StringComparison.Ordinal)) e.Cancel = true; };
        // Frames are refused, except the web page view's own sandboxed frame, which holds no address (Assets/web).
        core.FrameNavigationStarting += (_, e) => e.Cancel = !(page == "web/web.html" && e.Uri == "about:srcdoc");
        core.NewWindowRequested += (_, e) => e.Handled = true;          // no window is created
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, _) => opening?.TrySetException(new DocumentException("The document view stopped unexpectedly. Open the file again."));
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) { Block(e); return; }
        if (uri.Scheme == Uri.UriSchemeHttps && uri.Host == AppHost) return;   // served from the app's own folder
        if (uri.Scheme == Uri.UriSchemeHttps && uri.Host == DocumentHost && uri.AbsolutePath == "/" + resource && bytes is not null)
        {
            e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes, false), 200, "OK", JsonHeaders(contentType));
            return;
        }
        // Rows and searches of large sheets, and pictures placed on sheets, answered by the window on a background thread.
        if (uri.Scheme == Uri.UriSchemeHttps && uri.Host == DocumentHost && Data is { } data && uri.AbsolutePath is "/rows" or "/find" or "/media")
        {
            var deferral = e.GetDeferral();
            var environment = web.CoreWebView2.Environment;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            string path = uri.AbsolutePath;
            string type = path == "/media" ? ImageFiles.ContentTypeOf(query["name"] ?? "") ?? WebDocuments.FontContentType(query["name"] ?? "") ?? "application/octet-stream" : "application/json; charset=utf-8";
            _ = Task.Run(() =>
            {
                try { return data(path, query); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or FormatException or OverflowException or ObjectDisposedException) { return null; }
            }).ContinueWith(task => Dispatcher.InvokeAsync(() =>
            {
                e.Response = task.Result is { } body
                    ? environment.CreateWebResourceResponse(new MemoryStream(body, false), 200, "OK", JsonHeaders(type))
                    : environment.CreateWebResourceResponse(null, 404, "Not found", "");
                deferral.Complete();
            }), TaskScheduler.Default);
            return;
        }
        Block(e);
    }

    private static string JsonHeaders(string type) => $"Content-Type: {type}\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://{AppHost}";

    // Set by the window for large sheets: answers "/rows" and "/find" requests from the spreadsheet page.
    public Func<string, System.Collections.Specialized.NameValueCollection, byte[]?>? Data { get; set; }

    private void Block(CoreWebView2WebResourceRequestedEventArgs e)
    {
        BlockedRequests++;
        e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
    }

    // slides: one slide at a time with a thumbnail strip (converted PowerPoint files).
    public Task<int> LoadPdf(byte[] data, bool dark, CancellationToken cancellation, bool slides = false) =>
        Load("pdf/viewer.html", data, "document.pdf", "application/pdf", dark, cancellation, slides ? "&mode=slides" : "");

    public Task<int> LoadPicture(ImageFiles.Picture picture, bool dark, CancellationToken cancellation) =>
        Load("image/image.html", picture.Bytes, "picture", picture.ContentType, dark, cancellation, picture.Format == "SVG" ? "&vector=1" : "");

    public Task<int> LoadSheets(byte[] json, bool dark, CancellationToken cancellation) =>
        Load("sheet/sheet.html", json, "workbook.json", "application/json; charset=utf-8", dark, cancellation, "");

    // Web pages, saved web archives and EPUB books: the parts the worker collected (web.json).
    public Task<int> LoadWeb(byte[] json, bool dark, CancellationToken cancellation) =>
        Load("web/web.html", json, "web.json", "application/json; charset=utf-8", dark, cancellation, "");

    private async Task<int> Load(string pagePath, byte[] data, string name, string type, bool dark, CancellationToken cancellation, string query)
    {
        bytes = data; resource = name; contentType = type; page = pagePath; PageNotice = "";
        await EnsureReady();
        opening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Register(() => opening.TrySetCanceled(cancellation));
        web.CoreWebView2.Navigate($"https://{AppHost}/{pagePath}?theme={(dark ? "dark" : "light")}{query}");
        try { return await opening.Task; }
        catch { web.CoreWebView2?.Stop(); throw; }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith($"https://{AppHost}/", StringComparison.Ordinal)) return;
        using var json = JsonDocument.Parse(e.WebMessageAsJson);
        var message = json.RootElement;
        string type = message.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "loaded":
                Pages = message.TryGetProperty("pages", out var pages) ? pages.GetInt32() : message.GetProperty("sheets").GetInt32();
                PageNotice = message.TryGetProperty("notice", out var pageNotice) ? pageNotice.GetString() ?? "" : "";
                Page = 1;
                opening?.TrySetResult(Pages);
                break;
            case "state":
                if (message.TryGetProperty("page", out var p)) { Page = p.GetInt32(); Pages = message.GetProperty("pages").GetInt32(); }
                if (message.TryGetProperty("width", out var w)) { PictureWidth = w.GetInt32(); PictureHeight = message.GetProperty("height").GetInt32(); Rotation = message.GetProperty("rotation").GetInt32(); }
                else if (message.TryGetProperty("sheet", out var sheet)) { Page = sheet.GetInt32(); Pages = message.GetProperty("sheets").GetInt32(); SheetName = message.GetProperty("name").GetString() ?? ""; }
                Scale = message.GetProperty("scale").GetDouble();
                StateChanged?.Invoke();
                break;
            case "find":
                FindResult?.Invoke(message.GetProperty("current").GetInt32(), message.GetProperty("total").GetInt32(), message.GetProperty("done").GetBoolean());
                break;
            case "link":
                LinkRequested?.Invoke(message.GetProperty("href").GetString() ?? "");
                break;
            case "rendered":
                Rendered?.Invoke();
                break;
            case "password":
                string? password = AskPassword?.Invoke(message.GetProperty("incorrect").GetBoolean());
                Post(password is null ? new { type = "password-cancel" } : new { type = "password", value = password });
                break;
            case "error":
                opening?.TrySetException(new DocumentException(ErrorText(message.GetProperty("kind").GetString())));
                break;
        }
    }

    private static string ErrorText(string? kind) => kind switch
    {
        "password-cancelled" => "This PDF is password protected. Open it again and enter its password to view it.",
        "unavailable" => "The document could not be passed to the viewer. Open the file again.",
        "image" => "This picture is damaged or incomplete, or uses a variant of its format that cannot be shown. Try another copy of the file.",
        "web" => "This web page or book could not be shown. It may be damaged; try another copy of the file.",
        _ => "This PDF is damaged or incomplete, so it cannot be shown. Try another copy of the file."
    };

    private void Post(object message)
    {
        if (web.CoreWebView2 is not null) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    public void Find(string query, bool previous) => Post(new { type = "find", query, previous });
    public void Zoom(object value) => Post(new { type = "zoom", value });
    public void GoToPage(int number) => Post(new { type = "page", number });
    public void Step(int delta) => Post(new { type = "step", delta });
    public void ChangeSheet(int delta) => Post(new { type = "sheet", delta });
    public void Rotate(int delta) => Post(new { type = "rotate", delta });
    public void ShowThumbnails(bool show) => Post(new { type = "thumbnails", show });
    public void SetTheme(bool dark) => Post(new { type = "theme", dark });
    public void FocusDocument() { web.Focus(); Post(new { type = "focus" }); }

    // Test aid: saves what the view currently shows as a PNG. Works while the window is off-screen.
    public async Task Capture(string file)
    {
        if (web.CoreWebView2 is null) return;
        await using var stream = File.Create(file);
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }
}
