using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using RepartoCopier.Core;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace RepartoCopier.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<DestinationRow> _destinations = [];
    private readonly ObservableCollection<ProgressRow> _progressRows = [];
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private CopyJob? _job;
    private DateTimeOffset? _copyStartedAt;

    public MainWindow()
    {
        InitializeComponent();
        DestinationList.ItemsSource = _destinations;
        ProgressList.ItemsSource = _progressRows;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(980, 640));
        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        _progressTimer.Tick += ProgressTimer_Tick;
        Closed += MainWindow_Closed;
        _ = LoadBrandLogoAsync();
        TryLoadLaunchSource();
    }

    private async Task LoadBrandLogoAsync()
    {
        try
        {
            var bytes = Convert.FromBase64String(BrandAssets.LogoPngBase64);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            LogoImage.Source = bitmap;
        }
        catch { }
    }

    private void TryLoadLaunchSource()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 2 && !args[1].StartsWith("-", StringComparison.Ordinal)) SourcePathBox.Text = args[1];
        else if (args.Length == 3 && string.Equals(args[1], "--source", StringComparison.Ordinal)) SourcePathBox.Text = args[2];
    }

    private async void PickSourceFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id) { Title = "Selecciona el archivo que quieres copiar", CommitButtonText = "Seleccionar" };
            var result = await picker.PickSingleFileAsync();
            if (result is not null) SourcePathBox.Text = result.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void PickSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { Title = "Selecciona la carpeta que quieres copiar", CommitButtonText = "Seleccionar" };
            var result = await picker.PickSingleFolderAsync();
            if (result is not null) SourcePathBox.Text = result.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void AddDestinations_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { Title = "Selecciona dónde quieres guardar la copia", CommitButtonText = "Agregar" };
            var results = await picker.PickMultipleFoldersAsync();
            var paths = results.Select(result => result.Path).ToArray();
            var existing = _destinations.Select(item => item.Path).ToList();
            var added = CopyPlan.AppendUniqueDestinations(SourcePathBox.Text, existing, paths);
            if (added > 0)
            {
                _destinations.Clear();
                foreach (var path in existing) _destinations.Add(new DestinationRow(path));
            }
            DestinationCountText.Text = FormatDestinationCount(_destinations.Count);
            if (paths.Length > added && existing.Count >= CopyPlan.MaxDestinations)
                StatusText.Text = $"Se alcanzó el máximo de {CopyPlan.MaxDestinations} destinos.";
        }
        catch (Exception ex) { ShowError(ex.Message); }
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
            var plan = CopyPlan.Create(SourcePathBox.Text, _destinations.Select(item => item.Path), SkipSameCheck.IsChecked == true, KeepGoingCheck.IsChecked == true);
            var options = new CopyOptions(Verify: false, SkipSame: plan.SkipSame, KeepGoing: plan.KeepGoing);

            SetEditingEnabled(false);
            StartButton.IsEnabled = false;
            StatusText.Text = "Preparando la copia…";
            ShowRunningView();
            CurrentFileText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePathBox.Text));
            CurrentPathText.Text = SourcePathBox.Text;
            _copyStartedAt = DateTimeOffset.Now;

            _job = await CopyEngine.StartAsync(plan, options);
            PauseButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            StatusText.Text = plan.SkipSame ? "Comprobando archivos existentes…" : "Copiando…";
            _progressRows.Clear();
            foreach (var snapshot in _job.Snapshot()) _progressRows.Add(new ProgressRow(snapshot));
            RunningDestinationTitle.Text = $"Destinos ({_progressRows.Count})";
            _progressTimer.Start();
            _ = ObserveJobCompletionAsync(_job);
        }
        catch (Exception ex)
        {
            _job = null;
            _copyStartedAt = null;
            ShowPreparationView();
            SetEditingEnabled(true);
            StartButton.IsEnabled = true;
            ShowError(ex.Message);
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        var paused = !_job.IsPaused;
        _job.SetPaused(paused);
        PauseButtonText.Text = paused ? "Continuar" : "Pausar";
        PauseIcon.Glyph = paused ? "\uE768" : "\uE769";
        OperationTitleText.Text = paused ? "Pausado" : "Copiando...";
        StatusText.Text = paused ? "Pausado" : "Copiando…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        _job.RequestCancel();
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StatusText.Text = "Cancelando…";
    }

    private async Task ObserveJobCompletionAsync(CopyJob observed)
    {
        try { await observed.Completion; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            if (ReferenceEquals(_job, observed))
            {
                RefreshProgress();
                _progressTimer.Stop();
                var snapshots = observed.Snapshot();
                var failed = snapshots.Count(item => item.Phase == DestinationPhase.Failed);
                var cancelled = snapshots.Any(item => item.Phase == DestinationPhase.Cancelled);
                OperationTitleText.Text = cancelled ? "Cancelado" : failed > 0 ? "Completado con errores" : "Completado";
                StatusText.Text = cancelled ? "Copia cancelada" : failed > 0 ? "La copia terminó con algunos errores" : "Copia completada";
                PauseButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                await observed.DisposeAsync();
                _job = null;
                SetEditingEnabled(true);
                StartButton.IsEnabled = true;

                if (!cancelled && failed == 0 && ShutdownCheck.IsChecked == true)
                    await OfferShutdownAsync();
            }
        }
    }

    private void ProgressTimer_Tick(object? sender, object e) => RefreshProgress();

    private void RefreshProgress()
    {
        if (_job is null) return;
        var snapshots = _job.Snapshot();
        while (_progressRows.Count < snapshots.Count) _progressRows.Add(new ProgressRow(snapshots[_progressRows.Count]));
        for (var index = 0; index < snapshots.Count; index++) _progressRows[index].Update(snapshots[index]);

        var total = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => sum + item.Total);
        var written = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => sum + item.Written);
        var speed = snapshots.Aggregate<DestinationSnapshot, double>(0d, (sum, item) => sum + item.RecentBytesPerSecond);
        var percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);
        OverallProgressBar.Value = percent;
        OverallPercentText.Text = $"{percent:0}%";
        OverallDetailText.Text = $"{FormatBytes(written)} de {FormatBytes(total)}";
        SpeedMetricText.Text = Throughput.Format(speed);
        var filesDone = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => sum + item.FilesDone);
        FilesMetricText.Text = $"{filesDone}";

        var remaining = total > written ? total - written : 0;
        RemainingMetricText.Text = speed > 1 ? FormatDuration(TimeSpan.FromSeconds(remaining / speed)) : "--:--:--";
        if (_copyStartedAt is not null) ElapsedText.Text = $"Tiempo transcurrido: {FormatDuration(DateTimeOffset.Now - _copyStartedAt.Value)}";

        var current = snapshots.Select(item => item.LastFile).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(current)) CurrentFileText.Text = Path.GetFileName(current);

        if (!_job.IsPaused && snapshots.Any(item => item.Phase == DestinationPhase.Copying))
        {
            OperationTitleText.Text = "Copiando...";
            StatusText.Text = $"Copiando a {snapshots.Count} destino{(snapshots.Count == 1 ? string.Empty : "s")}…";
        }
    }

    private async void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null) return;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                Title = "Cargar copia",
                CommitButtonText = "Cargar",
                FileTypeChoices = { { "Configuración de RepartoCopier", new List<string> { ".rcopy" } } },
            };
            var result = await picker.PickSingleFileAsync();
            if (result is null) return;
            var profile = CopyProfileSerializer.Deserialize(await File.ReadAllTextAsync(result.Path));
            SourcePathBox.Text = profile.Source;
            _destinations.Clear();
            foreach (var path in profile.Destinations) _destinations.Add(new DestinationRow(path));
            DestinationCountText.Text = FormatDestinationCount(_destinations.Count);
            SkipSameCheck.IsChecked = profile.SkipExisting;
            KeepGoingCheck.IsChecked = profile.ContinueOnError;
            ShutdownCheck.IsChecked = profile.ShutdownWhenFinished;
            StatusText.Text = "Configuración cargada";
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = new CopyProfile(CopyProfile.CurrentVersion, SourcePathBox.Text, _destinations.Select(item => item.Path).ToArray(), SkipSameCheck.IsChecked == true, KeepGoingCheck.IsChecked == true, ShutdownCheck.IsChecked == true);
            var picker = new FileSavePicker(AppWindow.Id)
            {
                Title = "Guardar copia",
                CommitButtonText = "Guardar",
                SuggestedFileName = "Mi copia",
                DefaultFileExtension = ".rcopy",
                FileTypeChoices = { { "Configuración de RepartoCopier", new List<string> { ".rcopy" } } },
            };
            var result = await picker.PickSaveFileAsync();
            if (result is null) return;
            await File.WriteAllTextAsync(result.Path, CopyProfileSerializer.Serialize(profile));
            StatusText.Text = "Configuración guardada";
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var themeBox = new ComboBox { Header = "Tema", Width = 260 };
        themeBox.Items.Add(new ComboBoxItem { Content = "Sistema", Tag = "Default" });
        themeBox.Items.Add(new ComboBoxItem { Content = "Claro", Tag = "Light" });
        themeBox.Items.Add(new ComboBoxItem { Content = "Oscuro", Tag = "Dark" });
        themeBox.SelectedIndex = Root.RequestedTheme switch { ElementTheme.Light => 1, ElementTheme.Dark => 2, _ => 0 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Ajustes", PrimaryButtonText = "Aplicar", CloseButtonText = "Cerrar", Content = themeBox };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && themeBox.SelectedItem is ComboBoxItem item)
            Root.RequestedTheme = item.Tag?.ToString() switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "RepartoCopier", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Versión 2.0.0" });
        panel.Children.Add(new TextBlock { Text = "Copias rápidas y seguras para Windows.", Opacity = 0.7 });
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Acerca de", CloseButtonText = "Cerrar", Content = panel }.ShowAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async Task OfferShutdownAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Copia completada",
            Content = "El equipo se apagará en 60 segundos. Puedes cancelar el apagado desde Windows con shutdown /a.",
            PrimaryButtonText = "Apagar",
            CloseButtonText = "No apagar",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 60") { UseShellExecute = false, CreateNoWindow = true });
    }

    private void ShowRunningView()
    {
        PreparationPanel.Visibility = Visibility.Collapsed;
        RunningPanel.Visibility = Visibility.Visible;
        AppMenuButton.IsEnabled = true;
    }

    private void ShowPreparationView()
    {
        RunningPanel.Visibility = Visibility.Collapsed;
        PreparationPanel.Visibility = Visibility.Visible;
        ElapsedText.Text = string.Empty;
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
        StatusText.Text = "Ocurrió un error";
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _progressTimer.Stop();
        _job?.RequestCancel();
    }

    private static string FormatDestinationCount(int count) => count == 1 ? "1 destino" : $"{count} destinos";

    private static string FormatDuration(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    public sealed record DestinationRow(string Path);

    public sealed class ProgressRow : INotifyPropertyChanged
    {
        private string _label = string.Empty;
        private string _phaseText = string.Empty;
        private string _detail = string.Empty;
        private string _speed = string.Empty;
        private string _percentText = string.Empty;
        private double _percent;

        public ProgressRow(DestinationSnapshot snapshot) => Update(snapshot);
        public string Label { get => _label; private set => Set(ref _label, value); }
        public string PhaseText { get => _phaseText; private set => Set(ref _phaseText, value); }
        public string Detail { get => _detail; private set => Set(ref _detail, value); }
        public string Speed { get => _speed; private set => Set(ref _speed, value); }
        public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
        public double Percent { get => _percent; private set => Set(ref _percent, value); }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(DestinationSnapshot snapshot)
        {
            Label = snapshot.Label;
            PhaseText = snapshot.Phase switch
            {
                DestinationPhase.Idle => "Preparando",
                DestinationPhase.Copying => "Copiando",
                DestinationPhase.Verifying => "Comprobando",
                DestinationPhase.Done => "Completado",
                DestinationPhase.Failed => "Error",
                DestinationPhase.Cancelled => "Cancelado",
                _ => "Procesando",
            };
            Percent = snapshot.Total == 0 ? 100 : Math.Clamp(snapshot.Written * 100.0 / snapshot.Total, 0, 100);
            PercentText = $"{Percent:0}%";
            Detail = snapshot.Error ?? (snapshot.LastFile.Length == 0 ? $"{snapshot.FilesDone} archivo(s)" : snapshot.LastFile);
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
