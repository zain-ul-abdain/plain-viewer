using System.Windows;
namespace PlainViewer.App;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Remove private work folders left behind if the app or a conversion was ended abruptly.
        _ = Task.Run(() => { try { OfficeConverter.CleanLeftovers(); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { } });
        // Run by the installer: builds the Word/PowerPoint converter's profile so the first open is fast. Exit code 0 = ready.
        if (e.Args.FirstOrDefault() == "--prepare-converter")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await PrewarmConverter() ? 0 : 1);
            return;
        }
        // Test aid for scripts/compare-office.ps1: "--export-pdf <Word or PowerPoint file> <output.pdf>" converts the file
        // through the viewer's own checks and converter and saves the PDF it would show.
        if (e.Args.FirstOrDefault() == "--export-pdf" && e.Args.Length == 3)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var (_, pdf) = await OfficeConverter.Convert(e.Args[1], CancellationToken.None);
                await System.IO.File.WriteAllBytesAsync(e.Args[2], pdf);
                Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
            return;
        }
        // Timing and memory for scripts/measure.ps1: "--measure [file]" prints one JSON line.
        if (e.Args.FirstOrDefault() == "--measure")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await new MainWindow().MeasureAsync(e.Args.ElementAtOrDefault(1), System.Diagnostics.Process.GetCurrentProcess().StartTime);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
            Shutdown(result.ContainsKey("error") ? 1 : 0);
            return;
        }
        if (e.Args.FirstOrDefault() == "--smoke-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            PlainViewer.App.MainWindow.TestMode = true;
            try
            {
                foreach (var argument in e.Args.Skip(1))
                {
                    // "password=<test password>|<file>" opens a protected test file with that password.
                    string file = argument;
                    PlainViewer.App.MainWindow.TestPassword = null;
                    int bar = argument.IndexOf('|');
                    if (argument.TrimStart('!').StartsWith("password=") && bar > 0)
                    { PlainViewer.App.MainWindow.TestPassword = argument[(argument.IndexOf('=') + 1)..bar]; file = (argument.StartsWith('!') ? "!" : "") + argument[(bar + 1)..]; }
                    var preview = new MainWindow();
                    // A leading "!" means the file must be refused with a clear message (damaged, hostile, protected...).
                    if (file.StartsWith('!'))
                    {
                        string message = await preview.VerifyRefusedAsync(file[1..]);
                        Console.WriteLine($"PASS refused {System.IO.Path.GetFileName(file[1..])}: {message}");
                    }
                    else
                    {
                        await preview.VerifyPreviewAsync(file);
                        Console.WriteLine("PASS native view and worker: " + System.IO.Path.GetFileName(file));
                    }
                    preview.Close();
                }
                Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
            return;
        }
        AppSettings.Enabled = true;   // remembered theme and window placement, for interactive windows only
        var window = new MainWindow(); window.Show();
        if (e.Args.Length > 0) window.OpenPath(e.Args[0]);
        // Normally already done by the installer; repeats only if the profile is missing or LibreOffice changed.
        _ = PrewarmConverter();
    }

    private static async Task<bool> PrewarmConverter()
    {
        try { return await OfficeConverter.Prewarm(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlainViewer.Core.DocumentException) { return false; }
    }
}
