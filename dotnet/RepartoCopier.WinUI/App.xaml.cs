using Microsoft.UI.Xaml;
using RepartoCopier.Core;
using System.Text.Json;

namespace RepartoCopier.WinUI;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App() => InitializeComponent();

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
                Console.Error.WriteLine(ex);
            }
            Environment.Exit(status);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Activate();
        if (commandLine.Length == 3 && commandLine[1] == "--layout-check")
        {
            try { await MainWindow.RunLayoutCheckAsync(commandLine[2]); }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                await File.WriteAllTextAsync(commandLine[2], ex.ToString());
            }
            finally { MainWindow.Close(); }
        }
    }
}
