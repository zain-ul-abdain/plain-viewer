using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PlainViewer.Core;
namespace PlainViewer.App;

internal static class WorkerClient
{
    // How long one worker may take, and (tests only, tests/PlainViewer.Worker.Tests) a different program to start as the
    // worker: its file name and first arguments.
    internal static TimeSpan Timeout = TimeSpan.FromSeconds(20);
    internal static string[]? CommandForTests { get; set; }

    public const string Stopped = "The document worker stopped unexpectedly, possibly because the file needs more memory than the viewer allows for one document. Try again, or try a smaller file.";

    // Rows of CSV and large text files are written to a RowStore in `work`, which the caller deletes when done.
    // password: what the user typed for a protected file, or null. It reaches the worker on its standard input only.
    public static Task<DocumentView> Load(string path, string encoding, string delimiter, string work, CancellationToken cancellation, string? password = null) =>
        Run([path, encoding, delimiter, work], cancellation, password);

    // Validates a Word/PowerPoint package in the worker and writes a sanitised copy to `output` for conversion.
    public static Task<DocumentView> PrepareOffice(string path, string output, CancellationToken cancellation, string? password = null) =>
        Run([path, "--prepare-office", output], cancellation, password);

    private static async Task<DocumentView> Run(string[] arguments, CancellationToken cancellation, string? password = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(Timeout);
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        // Installed builds carry .NET with them and ship the worker as an .exe beside the app. Development builds run
        // the worker DLL with the local .NET host that scripts/env.ps1 sets.
        string published = Path.Combine(AppContext.BaseDirectory, "PlainViewer.Worker.exe");
        if (CommandForTests is { Length: > 0 } command) { start.FileName = command[0]; foreach (var part in command.Skip(1)) start.ArgumentList.Add(part); }
        else if (File.Exists(published) && File.Exists(Path.ChangeExtension(published, ".dll"))) start.FileName = published;
        else
        {
            start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "worker", "PlainViewer.Worker.dll"));
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new DocumentException("The document worker could not start. Rebuild the application and try again.");
        try
        {
            using var job = new WorkerJob(process);
            await process.StandardInput.WriteLineAsync("START");
            if (password is not null) await process.StandardInput.WriteLineAsync("PASSWORD " + Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
            process.StandardInput.Close();
            var errorDrain = process.StandardError.ReadToEndAsync(timeout.Token);
            var text = new StringBuilder(); char[] buffer = new char[8192]; int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer, timeout.Token)) != 0)
            { if (text.Length + count > 32 * 1024 * 1024) throw new DocumentException("This document exceeds the preview display limit."); text.Append(buffer, 0, count); }
            await process.WaitForExitAsync(timeout.Token); await errorDrain;
            // A worker that crashed, or was stopped at the memory limit, leaves no answer or half of one.
            WorkerResponse? response;
            try { response = JsonSerializer.Deserialize<WorkerResponse>(text.ToString()); }
            catch (JsonException) { throw new DocumentException(Stopped); }
            if (response?.Error is { } error) throw response.Password is { } state ? new PasswordException(error, state == "incorrect") : new DocumentException(error);
            return response?.Document ?? throw new DocumentException(Stopped);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new DocumentException($"Opening took longer than {Timeout.TotalSeconds:0} seconds. Try a smaller file."); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}

// Resource limits. Privileges are reduced by the worker itself, which drops to low integrity before reading (Program.cs).
internal sealed class WorkerJob : IDisposable
{
    private readonly IntPtr handle;
    public WorkerJob(Process process)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000 | 0x100 | 0x8; // kill on close, process memory, active process count
        limits.Basic.ActiveProcessLimit = 1; limits.ProcessMemoryLimit = (UIntPtr)(256UL * 1024 * 1024);
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(handle, process.Handle))
        { int error = Marshal.GetLastWin32Error(); Dispose(); throw new System.ComponentModel.Win32Exception(error); }
    }
    // Peak memory of the most recent worker, for scripts/measure.ps1.
    public static long LastPeakBytes { get; private set; }
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        var info = new ExtendedLimits();
        if (QueryInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero)) LastPeakBytes = (long)info.PeakProcessMemory;
        CloseHandle(handle);
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int info, ref ExtendedLimits limits, uint size, IntPtr returned);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr Minimum, Maximum; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
