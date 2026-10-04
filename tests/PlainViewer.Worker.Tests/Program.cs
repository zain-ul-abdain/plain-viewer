using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PlainViewer.App;
using PlainViewer.Core;

// Worker failure and recovery: the app's real WorkerClient (linked source) starts this program as a fake worker that
// crashes, hangs, floods its output, runs out of memory or starts another program, and then the real worker. Each
// failure must end in a clear message (or a cancellation), leave no worker process behind, and the next open must work.

// ---- Fake worker: "--fake-worker <mode> <file argument> <encoding> <delimiter> <work folder>" ----
if (args.FirstOrDefault() == "--fake-worker")
{
    if (Console.ReadLine() != "START") return 2;                   // the same handshake as the real worker
    string mode = args[1], file = args.ElementAtOrDefault(2) ?? "";
    void Answer(string text) => Console.Write(JsonSerializer.Serialize(new WorkerResponse(new DocumentView { Kind = "text", Text = text }, null)));
    switch (mode)
    {
        case "ok": Answer("hello"); return 0;
        case "job": Answer(IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out bool inJob) && inJob ? "in job" : "not in job"); return 0;
        case "crash": return 3;                                     // no answer at all
        case "garbage": Console.Write("{\"Document\": {\"Kind\": "); return 0;   // half an answer
        case "hang": File.WriteAllText(file, Environment.ProcessId.ToString()); Thread.Sleep(Timeout.Infinite); return 0;
        case "flood":
            {
                var chunk = new string('x', 1024 * 1024);
                for (int i = 0; i < 40; i++) Console.Write(chunk);
                return 0;
            }
        case "child":
            try
            {
                using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true });
                child?.WaitForExit();
                Answer("child started");
            }
            catch (Win32Exception) { Answer("child blocked"); }
            return 0;
        case "memory":
            {
                // Allocates and touches memory until the job's limit stops it; reports how far it got, in MB.
                var held = new List<byte[]>(); long total = 0;
                try
                {
                    while (total < 4L * 1024 * 1024 * 1024) { var block = new byte[16 * 1024 * 1024]; Array.Fill(block, (byte)1); held.Add(block); total += block.Length; }
                }
                catch (OutOfMemoryException) { }
                held.Clear(); GC.Collect();
                Answer((total / (1024 * 1024)).ToString());
                return 0;
            }
        default: return 4;
    }
}

// ---- Tests ----
int passed = 0, failed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
}
void Check(bool value, string what) { if (!value) throw new Exception(what); }

string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
string repo = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(repo, "PlainViewer.slnx"))) repo = Path.GetDirectoryName(repo) ?? throw new Exception("Repository root not found");
string realWorker = Path.Combine(repo, "src", "PlainViewer.Worker", "bin", "Release", "net10.0-windows", "PlainViewer.Worker.dll");
string root = Path.Combine(Path.GetTempPath(), "PlainViewerWorkerTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

Task<DocumentView> Fake(string mode, string file = "none", CancellationToken cancellation = default)
{
    WorkerClient.CommandForTests = [host, self, "--fake-worker", mode];
    return WorkerClient.Load(file, "Auto", "Auto", root, cancellation);
}
async Task<string> Refused(Func<Task> action)
{
    try { await action(); } catch (DocumentException ex) { return ex.Message; }
    throw new Exception("Expected a message, but the document opened");
}
// The worker whose process id the fake wrote must be gone soon after the client returns.
void Gone(string pidFile)
{
    int pid = int.Parse(File.ReadAllText(pidFile));
    for (int i = 0; i < 40; i++)
    {
        try { using var process = Process.GetProcessById(pid); if (process.HasExited) return; }
        catch (ArgumentException) { return; }
        Thread.Sleep(50);
    }
    throw new Exception($"Worker process {pid} is still running");
}
async Task Recovers() => Check((await Fake("ok")).Text == "hello", "the next open did not work");

await Test("A working worker answers", async () => Check((await Fake("ok")).Text == "hello", "no answer"));
await Test("The worker is in its job (memory and process limits) before it starts work", async () => Check((await Fake("job")).Text == "in job", "not in a job"));
await Test("A worker that crashes gives a clear message, and the next open works", async () =>
{
    Check(await Refused(() => Fake("crash")) == WorkerClient.Stopped, "unexpected message");
    await Recovers();
});
await Test("A worker that stops halfway through its answer gives the same message", async () =>
{
    Check(await Refused(() => Fake("garbage")) == WorkerClient.Stopped, "unexpected message");
    await Recovers();
});
await Test("Output past 32 MB is refused with the display limit message", async () =>
{
    Check((await Refused(() => Fake("flood"))).Contains("display limit", StringComparison.Ordinal), "unexpected message");
    await Recovers();
});
await Test("The worker cannot start another program (job process limit)", async () => Check((await Fake("child")).Text == "child blocked", "a child program started"));
await Test("The job's 256 MB memory limit stops a worker that keeps allocating", async () =>
{
    int reached = int.Parse((await Fake("memory")).Text);
    Check(reached is > 64 and <= 256, $"allocation stopped at {reached} MB");
    Console.WriteLine($"  (the fake worker's allocations stopped at {reached} MB)");
    await Recovers();
});
await Test("A worker that hangs is stopped at the time limit with a clear message", async () =>
{
    string pid = Path.Combine(root, "timeout.pid");
    var saved = WorkerClient.Timeout; WorkerClient.Timeout = TimeSpan.FromSeconds(2);
    var watch = Stopwatch.StartNew();
    try { Check(await Refused(() => Fake("hang", pid)) == "Opening took longer than 2 seconds. Try a smaller file.", "unexpected message"); }
    finally { WorkerClient.Timeout = saved; }
    Check(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    Gone(pid);
    await Recovers();
});
await Test("Cancel stops a worker at once and leaves no process", async () =>
{
    string pid = Path.Combine(root, "cancel.pid");
    using var cancel = new CancellationTokenSource();
    var open = Fake("hang", pid, cancel.Token);
    for (int i = 0; i < 200 && !File.Exists(pid); i++) await Task.Delay(50);   // the worker is running
    var watch = Stopwatch.StartNew();
    cancel.Cancel();
    bool cancelled = false;
    try { await open; } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "not cancelled");
    Check(watch.Elapsed < TimeSpan.FromSeconds(3), $"cancelling took {watch.Elapsed.TotalSeconds:0.0} s");
    Gone(pid);
    await Recovers();
});
await Test("The real worker opens a file after the failures above", async () =>
{
    Check(File.Exists(realWorker), "build the worker first: " + realWorker);
    WorkerClient.CommandForTests = [host, realWorker];
    var view = await WorkerClient.Load(Path.Combine(repo, "tests", "corpus", "simple.txt"), "Auto", "Auto", root, default);
    Check(view.Text.Contains("Hello", StringComparison.Ordinal), "text missing");
});
await Test("The real worker explains a file that has disappeared", async () =>
{
    WorkerClient.CommandForTests = [host, realWorker];
    string message = await Refused(() => WorkerClient.Load(Path.Combine(root, "missing.txt"), "Auto", "Auto", root, default));
    Check(message.Contains("moved or deleted", StringComparison.Ordinal), message);
});
await Test("The real worker asks for a protected workbook's password and opens it with the right one", async () =>
{
    WorkerClient.CommandForTests = [host, realWorker];
    string path = Path.Combine(repo, "tests", "corpus", "xlsx", "password.xlsx");
    async Task<PasswordException?> Asked(string? password)
    {
        try { await WorkerClient.Load(path, "Auto", "Auto", root, default, password); return null; }
        catch (PasswordException ex) { return ex; }
    }
    Check(await Asked(null) is { Incorrect: false }, "no password: not asked");
    Check(await Asked("not it\nPASSWORD dmlld2VyLXRlc3Q=") is { Incorrect: true }, "a password with a line break must stay one password");
    Check(await Asked("viewer-test") is null, "the right password did not open it");
    var view = await WorkerClient.Load(path, "Auto", "Auto", root, default, "viewer-test");
    Check(view.Kind == "sheet" && view.Sheets.Count > 0, "no sheets");
});

try { Directory.Delete(root, true); } catch (IOException) { }
Console.WriteLine($"Worker failure tests: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

[DllImport("kernel32.dll", SetLastError = true)] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
