using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using RepartoCopier.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace RepartoCopier.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DestinationRow> _destinations = [];
    private readonly ObservableCollection<ProgressRow> _progressRows = [];
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CopyJob? _job;
    private string? _lastDiagnosticsReport;

    public MainWindow()
    {
        InitializeComponent();
        DestinationList.ItemsSource = _destinations;
        ProgressList.ItemsSource = _progressRows;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(960, 620));

        _progressTimer.Tick += ProgressTimer_Tick;
        Closed += MainWindow_Closed;
        TryLoadLaunchSource();
    }

    private void TryLoadLaunchSource()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 2 && !args[1].StartsWith("-", StringComparison.Ordinal))
            SourcePathBox.Text = args[1];
        else if (args.Length == 3 && string.Equals(args[1], "--source", StringComparison.Ordinal))
            SourcePathBox.Text = args[2];
    }

    private async void PickSourceFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                Title = "Selecciona el archivo de origen",
                CommitButtonText = "Usar archivo",
            };
            var result = await picker.PickSingleFileAsync();
            if (result is not null)
                SourcePathBox.Text = result.Path;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void PickSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id)
            {
                Title = "Selecciona la carpeta de origen",
                CommitButtonText = "Usar carpeta",
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is not null)
                SourcePathBox.Text = result.Path;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void AddDestinations_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id)
            {
                Title = "Selecciona uno o más destinos",
                CommitButtonText = "Agregar",
            };
            var results = await picker.PickMultipleFoldersAsync();
            var paths = results.Select(result => result.Path).ToArray();
            var existing = _destinations.Select(item => item.Path).ToList();
            var added = CopyPlan.AppendUniqueDestinations(SourcePathBox.Text, existing, paths);
            if (added > 0)
            {
                _destinations.Clear();
                foreach (var path in existing)
                    _destinations.Add(new DestinationRow(path));
            }
            DestinationCountText.Text = FormatDestinationCount(_destinations.Count);
            if (paths.Length > added && existing.Count >= CopyPlan.MaxDestinations)
                StatusText.Text = $"Se alcanzó el máximo de {CopyPlan.MaxDestinations} destinos.";
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RemoveDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        var item = _destinations.FirstOrDefault(row => WindowsPath.SamePath(row.Path, path));
        if (item is not null) _destinations.Remove(item);
        DestinationCountText.Text = FormatDestinationCount(_destinations.Count);
    }

    private void ClearDestinations_Click(object sender, RoutedEventArgs e)
    {
        _destinations.Clear();
        DestinationCountText.Text = FormatDestinationCount(0);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null) return;
        try
        {
            ErrorBar.IsOpen = false;
            _lastDiagnosticsReport = null;
            CopyDiagnosticsButton.IsEnabled = false;
            var plan = CopyPlan.Create(
                SourcePathBox.Text,
                _destinations.Select(item => item.Path),
                SkipSameCheck.IsChecked == true,
                KeepGoingCheck.IsChecked == true);
            var options = new CopyOptions(
                Verify: true,
                SkipSame: plan.SkipSame,
                KeepGoing: plan.KeepGoing);

            SetEditingEnabled(false);
            StartButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            PauseButton.Content = "Pausar";
            StatusText.Text = "Analizando origen y destinos…";

            _job = await CopyEngine.StartAsync(plan, options);
            PauseButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            StatusText.Text = plan.SkipSame
                ? "Comparando archivos existentes con BLAKE3…"
                : "Iniciando copia FAN-OUT…";
            _progressRows.Clear();
            foreach (var snapshot in _job.Snapshot())
                _progressRows.Add(new ProgressRow(snapshot));
            _progressTimer.Start();
            _ = ObserveJobCompletionAsync(_job);
        }
        catch (Exception ex)
        {
            _job = null;
            SetEditingEnabled(true);
            StartButton.IsEnabled = true;
            PauseButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            ShowError(ex.Message);
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        var paused = !_job.IsPaused;
        _job.SetPaused(paused);
        PauseButton.Content = paused ? "Continuar" : "Pausar";
        StatusText.Text = paused ? "Pausado" : "Copiando…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        _job.RequestCancel();
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StatusText.Text = "Cancelando de forma segura…";
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastDiagnosticsReport)) return;
        var package = new DataPackage();
        package.SetText(_lastDiagnosticsReport);
        Clipboard.SetContent(package);
        StatusText.Text = "Diagnóstico copiado al portapapeles";
    }

    private async Task ObserveJobCompletionAsync(CopyJob observed)
    {
        try
        {
            await observed.Completion;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_job, observed))
            {
                RefreshProgress();
                _progressTimer.Stop();
                var snapshots = observed.Snapshot();
                _lastDiagnosticsReport = DiagnosticsReport.Format(
                    SourcePathBox.Text,
                    snapshots,
                    observed.DiagnosticsSnapshot());
                CopyDiagnosticsButton.IsEnabled = true;
                var failed = snapshots.Count(item => item.Phase == DestinationPhase.Failed);
                var cancelled = snapshots.Any(item => item.Phase == DestinationPhase.Cancelled);
                StatusText.Text = cancelled
                    ? "Copia cancelada"
                    : failed > 0
                        ? $"Finalizado con {failed} destino(s) fallido(s)"
                        : "Copia completada y verificada";
                await observed.DisposeAsync();
                _job = null;
                SetEditingEnabled(true);
                StartButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                PauseButton.Content = "Pausar";
            }
        }
    }

    private void ProgressTimer_Tick(object? sender, object e) => RefreshProgress();

    private void RefreshProgress()
    {
        if (_job is null) return;
        var snapshots = _job.Snapshot();
        while (_progressRows.Count < snapshots.Count)
            _progressRows.Add(new ProgressRow(snapshots[_progressRows.Count]));
        for (var index = 0; index < snapshots.Count; index++)
            _progressRows[index].Update(snapshots[index]);

        OverallSpeedText.Text = Throughput.Format(snapshots.Sum(item => item.RecentBytesPerSecond));
        if (!_job.IsPaused && snapshots.Any(item => item.Phase == DestinationPhase.Verifying))
            StatusText.Text = "Verificando físicamente los destinos…";
        else if (!_job.IsPaused && snapshots.Any(item => item.Phase == DestinationPhase.Copying))
            StatusText.Text = "Copiando en FAN-OUT…";
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null || ThemeSelector.SelectedItem is not ComboBoxItem item) return;
        Root.RequestedTheme = item.Tag?.ToString() switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void SetEditingEnabled(bool enabled)
    {
        SourcePathBox.IsEnabled = enabled;
        DestinationList.IsEnabled = enabled;
        SkipSameCheck.IsEnabled = enabled;
        KeepGoingCheck.IsEnabled = enabled;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        StatusText.Text = "Error";
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _progressTimer.Stop();
        _job?.RequestCancel();
    }

    private static string FormatDestinationCount(int count) =>
        count == 1 ? "1 destino" : $"{count} destinos";

    public sealed record DestinationRow(string Path);

    public sealed class ProgressRow : INotifyPropertyChanged
    {
        private string _label = string.Empty;
        private string _phaseText = string.Empty;
        private string _detail = string.Empty;
        private string _speed = string.Empty;
        private double _percent;

        public ProgressRow(DestinationSnapshot snapshot) => Update(snapshot);

        public string Label { get => _label; private set => Set(ref _label, value); }
        public string PhaseText { get => _phaseText; private set => Set(ref _phaseText, value); }
        public string Detail { get => _detail; private set => Set(ref _detail, value); }
        public string Speed { get => _speed; private set => Set(ref _speed, value); }
        public double Percent { get => _percent; private set => Set(ref _percent, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(DestinationSnapshot snapshot)
        {
            Label = snapshot.Label;
            PhaseText = snapshot.Phase switch
            {
                DestinationPhase.Idle => "Preparando",
                DestinationPhase.Copying => "Copiando",
                DestinationPhase.Verifying => "Verificando",
                DestinationPhase.Done => "Completado",
                DestinationPhase.Failed => "Error",
                DestinationPhase.Cancelled => "Cancelado",
                _ => snapshot.Phase.ToString(),
            };
            Percent = snapshot.Total == 0 ? 100 : Math.Clamp(snapshot.Written * 100.0 / snapshot.Total, 0, 100);
            Detail = snapshot.Error ?? (snapshot.LastFile.Length == 0
                ? $"{snapshot.FilesDone} archivo(s)"
                : snapshot.LastFile);
            Speed = Throughput.Format(snapshot.RecentBytesPerSecond);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
