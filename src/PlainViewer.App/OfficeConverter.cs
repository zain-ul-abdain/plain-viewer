using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PlainViewer.Core;
namespace PlainViewer.App;

// Converts a sanitised Word/PowerPoint copy to PDF with the bundled LibreOffice.
// LibreOffice cannot run inside an AppContainer (it always creates a machine-wide named pipe), so it runs at low
// integrity in a Job Object with memory, process-count and UI limits, from a private profile whose hardened settings
// are rewritten on every run; optional firewall rules added by the installer block its network access. The input is
// a private copy already stripped of outside references by the worker. See DECISIONS.md, gate 1.
internal static class OfficeConverter
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);
    private static string AppData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainViewer");
    // LibreOffice runs at low integrity, so its profile and work folders live where low-integrity writes are allowed.
    private static string Profile => Path.Combine(LowIntegrity.Root, "LibreOffice", "profile");
    // Written once LibreOffice has built the profile; holds the LibreOffice build it was built with.
    private static string ReadyMarker => Path.Combine(Profile, "user", "plainviewer-ready.txt");

    public static string? FindLibreOffice()
    {
        if (Environment.GetEnvironmentVariable("PLAINVIEWER_LIBREOFFICE") is { Length: > 0 } configured && File.Exists(configured)) return configured;
        string installed = Path.Combine(AppContext.BaseDirectory, "libreoffice", "program", "soffice.exe");
        if (File.Exists(installed)) return installed;
        // Development builds: the unpacked copy under the repository's .tools folder (scripts/fetch-libreoffice.ps1).
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var tools = new DirectoryInfo(Path.Combine(dir.FullName, ".tools"));
            if (!tools.Exists) continue;
            // libreoffice-<version> is the x64 copy, libreoffice-<version>-arm64 the ARM64 one (package.ps1 -Arch arm64).
            bool arm64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
            var found = tools.GetDirectories("libreoffice-*").Where(d => d.Name.EndsWith("-arm64", StringComparison.Ordinal) == arm64)
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .Select(d => Path.Combine(d.FullName, "program", "soffice.exe")).FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }
        return null;
    }

    private static string WorkRoot => Path.Combine(LowIntegrity.Root, "Temp");

    public static string NewWorkFolder()
    {
        string folder = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "in"));
        return folder;
    }

    public static void Delete(string folder)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    // Removes work folders left behind by a crash (called at start-up).
    public static void CleanLeftovers()
    {
        // Also the folder used before the converter ran at low integrity (version 0.1.0).
        foreach (var root in new[] { WorkRoot, Path.Combine(AppData, "Temp") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var folder in Directory.GetDirectories(root))
                if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddMinutes(-10)) Delete(folder);
        }
    }

    // The whole Word/PowerPoint path: the worker checks the package and writes a private copy without outside
    // references, then LibreOffice converts that copy. `converting` runs between the two steps (status text).
    // password: for a protected Word or PowerPoint file; the worker decrypts it and writes only the cleaned copy.
    public static async Task<(DocumentView Prepared, byte[] Pdf)> Convert(string path, CancellationToken cancellation, Action? converting = null, string? password = null)
    {
        string work = NewWorkFolder();
        try
        {
            var (prepared, copy) = await Prepare(path, work, cancellation, password);
            converting?.Invoke();
            return (prepared, await ToPdf(copy, work, cancellation));
        }
        finally { Delete(work); }
    }

    // The worker checks the file and writes a cleaned private copy. OOXML variants are relabelled as plain .docx/.pptx;
    // other formats are named after their content (for example an RTF saved with a .doc name).
    private static async Task<(DocumentView Prepared, string Copy)> Prepare(string path, string work, CancellationToken cancellation, string? password = null)
    {
        bool converted = ConvertedDocuments.Handles(path);
        string copy = Path.Combine(work, "in", converted ? "document" + Path.GetExtension(path).ToLowerInvariant() : OfficePackages.IsWord(path) ? "document.docx" : "document.pptx");
        var prepared = await WorkerClient.PrepareOffice(path, copy, cancellation, password);
        if (converted && Path.ChangeExtension(copy, ConvertedDocuments.CopyExtension(prepared)) is var named && named != copy)
        { File.Move(copy, named); copy = named; }
        return (prepared, copy);
    }

    public static async Task<byte[]> ToPdf(string input, string work, CancellationToken cancellation) =>
        await File.ReadAllBytesAsync(await ToFormat(input, work, "pdf", cancellation), cancellation);

    private static async Task<string> ToFormat(string input, string work, string format, CancellationToken cancellation)
    {
        string soffice = FindLibreOffice() ?? throw new DocumentException("Viewing this kind of file needs the document converter, which is not installed. Reinstall Plain Viewer to add it.");
        string output = Path.Combine(work, "out"), temp = Path.Combine(work, "tmp");
        Directory.CreateDirectory(output); Directory.CreateDirectory(temp);
        using var gate = await Acquire(cancellation);
        string pdf = Path.Combine(output, Path.GetFileNameWithoutExtension(input) + "." + format);
        for (int attempt = 1; ; attempt++)
        {
            PrepareProfile(soffice);
            int exit = await Task.Run(() => RunLimited(soffice, $"{CommonArguments} --convert-to {format} --outdir \"{output}\" \"{input}\"", temp, cancellation), cancellation);
            if (exit == 0 && File.Exists(pdf) && new FileInfo(pdf).Length > 0) break;
            // A profile LibreOffice never finished building (no ready marker) can make it quit without converting:
            // start again from a new profile, once.
            if (attempt == 1 && !IsReady(soffice)) { try { Directory.Delete(Profile, true); } catch (DirectoryNotFoundException) { } continue; }
            throw new DocumentException("This document could not be prepared for viewing. It may be damaged or use features this viewer cannot show. Try another copy of the file.");
        }
        MarkReady(soffice);
        return pdf;
    }

    // Builds the private LibreOffice profile before the first Word or PowerPoint file is opened. A new profile adds
    // about 10 seconds to LibreOffice's first conversion, and starting LibreOffice alone does not do that work, so this
    // converts two tiny bundled documents (copies of the test corpus's simple.docx and simple.pptx). Runs with the same
    // limits and settings as a conversion. Returns true when the profile is ready (already, or now).
    public static async Task<bool> Prewarm(CancellationToken cancellation = default)
    {
        string? soffice = FindLibreOffice();
        if (soffice is null) return false;
        if (IsReady(soffice)) return true;
        using var gate = await Acquire(cancellation);
        if (IsReady(soffice)) return true;   // another window finished it while this one waited
        // A profile without the marker may be half-built (for example, the app was closed during a pre-warm): start again.
        try { Directory.Delete(Profile, true); } catch (DirectoryNotFoundException) { }
        string work = NewWorkFolder(), output = Path.Combine(work, "out"), temp = Path.Combine(work, "tmp");
        string samples = Path.Combine(AppContext.BaseDirectory, "Assets", "prewarm");
        try
        {
            Directory.CreateDirectory(output); Directory.CreateDirectory(temp);
            PrepareProfile(soffice);
            string[] names = ["prewarm-word.docx", "prewarm-slides.pptx"];
            string inputs = string.Join(' ', names.Select(name => $"\"{Path.Combine(samples, name)}\""));
            int exit = await Task.Run(() => RunLimited(soffice, $"{CommonArguments} --convert-to pdf --outdir \"{output}\" {inputs}", temp, cancellation), cancellation);
            if (exit != 0 || !names.All(name => File.Exists(Path.Combine(output, Path.ChangeExtension(name, ".pdf"))))) return false;
            MarkReady(soffice);
            return true;
        }
        finally { Delete(work); }
    }

    private static string CommonArguments => $"--headless --norestore --nologo --nodefault --nolockcheck \"-env:UserInstallation={FileUrl(Profile)}\"";

    // One LibreOffice run at a time across all Plain Viewer windows, because they share one profile.
    private static async Task<IDisposable> Acquire(CancellationToken cancellation)
    {
        var gate = new Semaphore(1, 1, @"Local\PlainViewer.LibreOffice");
        try
        {
            await Task.Run(() => { while (!gate.WaitOne(200)) cancellation.ThrowIfCancellationRequested(); }, cancellation);
            return new Held(gate);
        }
        catch { gate.Dispose(); throw; }
    }

    private sealed class Held(Semaphore gate) : IDisposable
    {
        public void Dispose() { gate.Release(); gate.Dispose(); }
    }

    // The LibreOffice build and folder the profile was made with; a different LibreOffice gets a fresh profile.
    private static string Stamp(string soffice) => "buildid=" + BuildId(soffice) + "\n" + Path.GetDirectoryName(soffice);

    private static string BuildId(string soffice) =>
        File.ReadLines(Path.Combine(Path.GetDirectoryName(soffice)!, "version.ini")).FirstOrDefault(line => line.StartsWith("buildid=", StringComparison.Ordinal))?["buildid=".Length..] ?? "";

    private static bool IsReady(string soffice)
    {
        try { return File.Exists(ReadyMarker) && File.ReadAllText(ReadyMarker) == Stamp(soffice); }
        catch (IOException) { return false; }
    }

    private static void MarkReady(string soffice)
    {
        try { if (!IsReady(soffice)) File.WriteAllText(ReadyMarker, Stamp(soffice)); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FileUrl(string path) => "file:///" + path.Replace('\\', '/');

    private static void PrepareProfile(string soffice)
    {
        string user = Path.Combine(Profile, "user");
        Directory.CreateDirectory(user);
        // Rewritten before every run: restores the hardened settings and clears LibreOffice's history.
        File.WriteAllText(Path.Combine(user, "registrymodifications.xcu"), HardenedSettings(IsReady(soffice) ? BuildId(soffice) : null), new UTF8Encoding(false));
        foreach (var leftover in new[] { "backup", "temp" })
            try { Directory.Delete(Path.Combine(user, leftover), true); } catch (DirectoryNotFoundException) { } catch (IOException) { }
    }

    // Values checked against LibreOffice's configuration schema (see DECISIONS.md). "Never" is 2 for Writer and 1 for Calc.
    // readyBuild: once the profile is built, LibreOffice's own record that set-up and its extension check are done for
    // this build. Without it every run repeats that check and may restart LibreOffice, which reset some of these
    // settings to their defaults (found by the Office comparison tests).
    private static string HardenedSettings(string? readyBuild)
    {
        var items = new List<(string Path, string Name, string Value)>
        {
            ("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel", "3"),
            ("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution", "true"),
            ("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks", "true"),
            ("/org.openoffice.Office.Writer/Content/Update", "Link", "2"),
            ("/org.openoffice.Office.Writer/Content/Update", "Field", "false"),
            ("/org.openoffice.Office.Calc/Content/Update", "Link", "1"),
            ("/org.openoffice.Office.Calc/Formula/Load", "OOXMLRecalcMode", "1"),
            ("/org.openoffice.Office.Calc/Formula/Load", "ODFRecalcMode", "1"),
            ("/org.openoffice.Office.Common/Misc", "UseDocumentOOoLockFile", "false"),
            ("/org.openoffice.Office.Common/Misc", "UseDocumentSystemFileLocking", "false"),
            ("/org.openoffice.Office.Common/Misc", "CrashReport", "false"),
        };
        // Extra layer: send any web request LibreOffice makes to a closed local port. Tests switch this off so the
        // request listener can observe LibreOffice directly.
        if (Environment.GetEnvironmentVariable("PLAINVIEWER_LO_PROXY_OFF") != "1")
            items.AddRange([
                ("/org.openoffice.Inet/Settings", "ooInetProxyType", "2"),
                ("/org.openoffice.Inet/Settings", "ooInetHTTPProxyName", "127.0.0.1"), ("/org.openoffice.Inet/Settings", "ooInetHTTPProxyPort", "9"),
                ("/org.openoffice.Inet/Settings", "ooInetHTTPSProxyName", "127.0.0.1"), ("/org.openoffice.Inet/Settings", "ooInetHTTPSProxyPort", "9"),
                ("/org.openoffice.Inet/Settings", "ooInetFTPProxyName", "127.0.0.1"), ("/org.openoffice.Inet/Settings", "ooInetFTPProxyPort", "9"),
                ("/org.openoffice.Inet/Settings", "ooInetNoProxy", "")]);
        if (readyBuild is { Length: > 0 })
            items.AddRange([("/org.openoffice.Setup/Office", "ooSetupInstCompleted", "true"), ("/org.openoffice.Setup/Office", "LastCompatibilityCheckID", System.Security.SecurityElement.Escape(readyBuild))]);
        var replacements = FontReplacements();
        items.Add(("/org.openoffice.Office.Common/Font/Substitution", "Replacement", replacements.Count > 0 ? "true" : "false"));
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n");
        foreach (var (path, name, value) in items)
            xml.Append($"<item oor:path=\"{path.Replace("'", "&apos;")}\"><prop oor:name=\"{name}\" oor:op=\"fuse\"><value>{value}</value></prop></item>\n");
        for (int i = 0; i < replacements.Count; i++)
            xml.Append($"<item oor:path=\"/org.openoffice.Office.Common/Font/Substitution/FontPairs\"><node oor:name=\"_{i}\" oor:op=\"replace\">")
               .Append("<prop oor:name=\"Always\" oor:op=\"fuse\"><value>true</value></prop><prop oor:name=\"OnScreenOnly\" oor:op=\"fuse\"><value>false</value></prop>")
               .Append($"<prop oor:name=\"ReplaceFont\" oor:op=\"fuse\"><value>{System.Security.SecurityElement.Escape(replacements[i].Missing)}</value></prop>")
               .Append($"<prop oor:name=\"SubstituteFont\" oor:op=\"fuse\"><value>{System.Security.SecurityElement.Escape(replacements[i].Use)}</value></prop></node></item>\n");
        // A property added to an extensible group needs its type. Without it LibreOffice rejected this entry and ignored
        // every entry after it (found in September 2026; the update check had stayed on). Written last as a precaution.
        xml.Append("<item oor:path=\"/org.openoffice.Office.Jobs/Jobs/org.openoffice.Office.Jobs:Job[&apos;UpdateCheck&apos;]/Arguments\">")
           .Append("<prop oor:name=\"AutoCheckEnabled\" oor:op=\"fuse\" oor:type=\"xs:boolean\"><value>false</value></prop></item>\n");
        return xml.Append("</oor:items>\n").ToString();
    }

    // Fonts that Office documents often use but many Windows PCs lack. Left to itself, LibreOffice may pick a much wider
    // font (for Arabic it chose Arial Black), which pushes text onto extra pages. A rule is written only for a font that
    // is not installed, so an installed font is always used. PLAINVIEWER_FONT_REPLACEMENTS ("Font=Font;...") replaces
    // the list, for comparison tests.
    private static readonly (string Missing, string Use)[] DefaultReplacements =
    [
        ("Simplified Arabic", "Arial"), ("Simplified Arabic Fixed", "Courier New"), ("Traditional Arabic", "Times New Roman"),
        ("Arabic Typesetting", "Times New Roman"), ("Sakkal Majalla", "Arial"), ("Andalus", "Arial"),
        ("SimHei", "Microsoft YaHei"), ("黑体", "Microsoft YaHei"), ("DengXian", "Microsoft YaHei"), ("等线", "Microsoft YaHei"),
    ];

    private static List<(string Missing, string Use)> FontReplacements()
    {
        var pairs = Environment.GetEnvironmentVariable("PLAINVIEWER_FONT_REPLACEMENTS") is { Length: > 0 } configured
            ? configured.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2)).Where(p => p.Length == 2).Select(p => (p[0].Trim(), p[1].Trim())).ToArray()
            : DefaultReplacements;
        var installed = Installed.Value;
        return pairs.Where(pair => !installed.Contains(pair.Item1) && installed.Contains(pair.Item2)).ToList();
    }

    private static readonly Lazy<HashSet<string>> Installed = new(InstalledFonts);
    private static HashSet<string> InstalledFonts()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
            foreach (var name in family.FamilyNames.Values) names.Add(name);
        // Fonts installed for the current user only are not always in SystemFontFamilies.
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
        foreach (var name in key?.GetValueNames() ?? []) names.Add(System.Text.RegularExpressions.Regex.Replace(name, @" \(.*\)$", ""));
        return names;
    }

    private static int RunLimited(string exe, string arguments, string temp, CancellationToken cancellation)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000 | 0x200 | 0x8 | 0x400;   // kill on close, job memory, active processes, die on unhandled exception
            // The launcher (soffice.exe) and one soffice.bin; LibreOffice may not start any other program.
            limits.Basic.ActiveProcessLimit = 2;
            limits.JobMemoryLimit = (UIntPtr)(3UL * 1024 * 1024 * 1024);
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception();
            uint ui = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x40 | 0x80;   // no outside USER handles, clipboard, system settings, global atoms, desktops or log-off
            if (!SetInformationJobObject(job, 4, ref ui, 4)) throw new Win32Exception();

            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var environment = new StringBuilder();
            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                string key = (string)pair.Key;
                if (key is "TEMP" or "TMP" || key.StartsWith("PLAINVIEWER_", StringComparison.Ordinal)) continue;
                environment.Append(key).Append('=').Append(pair.Value).Append('\0');
            }
            environment.Append("TEMP=").Append(temp).Append('\0').Append("TMP=").Append(temp).Append('\0').Append('\0');
            const uint Suspended = 0x4, NoWindow = 0x08000000, UnicodeEnvironment = 0x400;
            // Started at low integrity: LibreOffice and anything it starts cannot write the user's files or change other programs.
            IntPtr token = LowIntegrity.CreateToken();
            ProcessInformation process;
            try
            {
                if (!CreateProcessAsUser(token, exe, new StringBuilder($"\"{exe}\" {arguments}"), IntPtr.Zero, IntPtr.Zero, false, Suspended | NoWindow | UnicodeEnvironment,
                        environment.ToString(), temp, ref startup, out process))
                    throw new Win32Exception();
            }
            finally { CloseHandle(token); }
            try
            {
                if (!AssignProcessToJobObject(job, process.Process)) { TerminateProcess(process.Process, 1); throw new Win32Exception(); }
                ResumeThread(process.Thread);
                var deadline = DateTime.UtcNow + Limit;
                // Wait for the launcher and then for every process it started (soffice.exe hands work to soffice.bin).
                while (true)
                {
                    bool launcherRunning = WaitForSingleObject(process.Process, 200) == 0x102;
                    if (!launcherRunning && ActiveProcesses(job) == 0) break;
                    if (!launcherRunning) Thread.Sleep(200);
                    cancellation.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > deadline) throw new DocumentException("Preparing this document took longer than two minutes, so it was stopped. The file may be very large or damaged.");
                }
                GetExitCodeProcess(process.Process, out uint code);
                return (int)code;
            }
            finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
        }
        finally
        {
            var usage = new ExtendedLimits();
            if (QueryInformationJobObject(job, 9, ref usage, (uint)Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero)) LastPeakBytes = (long)usage.PeakJobMemory;
            CloseHandle(job);   // kill-on-close ends LibreOffice and anything it started
        }
    }

    // Peak memory of the most recent LibreOffice run (all its processes together), for scripts/measure.ps1.
    public static long LastPeakBytes { get; private set; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint size, IntPtr returned);

    private static uint ActiveProcesses(IntPtr job)
    {
        var info = new BasicAccounting();
        return QueryInformationJobObject(job, 1, ref info, (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero) ? info.ActiveProcesses : 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr Minimum, Maximum; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting { public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string? lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref uint info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref BasicAccounting info, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, string environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
