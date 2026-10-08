using Microsoft.UI.Xaml;
using RepartoCopier.Core;
using System.Text.Json;

namespace RepartoCopier.WinUI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        // A crash leaves a trace the user can send: the window itself has no console.
        UnhandledException += (_, args) => WriteCrashLog(args.Exception);
    }

    internal static void WriteCrashLog(Exception error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepartoCopier");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "errores.log"),
                $"{DateTimeOffset.Now:O} {BuildInfo.InformationalVersion(typeof(App).Assembly)}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never turn into a second failure.
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1 && commandLine[1] == "--source-bench")
        {
            var status = 0;
            try
            {
                var request = SourceBenchmarkCommandLine.Parse(commandLine[1..]);
                var measurements = new List<SourceReadBenchmarkResult>();
                foreach (var count in request.Readers)
                    measurements.Add(await SourceReadBenchmark.RunAsync(request.Source, count, request.Seconds));
                var report = new
                {
                    SchemaVersion = 1,
                    BuildRevision = BuildInfo.Revision(BuildInfo.InformationalVersion(typeof(App).Assembly)),
                    MeasurementNotes = "Lectura y BLAKE3 por lector, sin escrituras ni VERIFY. Cada lector repite el archivo completo; la duración puede superar --seconds para completar la última pasada. El ancho de banda agregado incluye relecturas y no predice por sí solo COPY.",
                    Runs = measurements,
                };
                await using var output = new FileStream(request.Report, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                status = 1;
                // A WinExe has no console: keep the reason next to the requested report.
                try
                {
                    var report = commandLine.Length > 3 ? commandLine[3] : null;
                    if (report is not null) await File.WriteAllTextAsync(report + ".error.txt", ex.ToString());
                }
                catch { }
                WriteCrashLog(ex);
            }
            Environment.Exit(status);
            return;
        }
        var window = new MainWindow();
        window.Activate();
        if (commandLine.Length == 3 && commandLine[1] == "--layout-check")
        {
            try { await window.RunLayoutCheckAsync(commandLine[2]); }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                try { await File.WriteAllTextAsync(commandLine[2], ex.ToString()); }
                catch (Exception writeError) { WriteCrashLog(new AggregateException(ex, writeError)); }
            }
            finally { window.Close(); }
        }
    }
}
