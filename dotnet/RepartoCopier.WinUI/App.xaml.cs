using Microsoft.UI.Xaml;

namespace RepartoCopier.WinUI;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
        var commandLine = Environment.GetCommandLineArgs();
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
