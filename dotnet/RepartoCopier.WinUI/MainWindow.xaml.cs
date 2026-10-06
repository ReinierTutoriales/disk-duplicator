using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using RepartoCopier.Core;
using Windows.Graphics;

namespace RepartoCopier.WinUI;

public sealed partial class MainWindow : Window
{
    private const string ProjectUrl = "https://github.com/ReinierTutoriales/disk-duplicator";
    private const string LicenseUrl = "https://github.com/ReinierTutoriales/disk-duplicator/blob/main/LICENSE";

    private readonly ObservableCollection<DestinationRow> _destinations = [];
    private readonly ObservableCollection<RunningDestinationRow> _runningDestinations = [];
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CopyJob? _job;
    private CancellationTokenSource? _preparationCancel;
    private bool _closeRequested;
    private bool _cancellationRequested;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private IReadOnlyList<DestinationSnapshot> _lastResult = [];
    private CopyDiagnosticsSnapshot? _lastDiagnostics;
    private DateTimeOffset? _copyStartedAt;
    private readonly LogicalProgressRate _copyProgressRate = new();

    public MainWindow()
    {
        InitializeComponent();
        DestinationList.ItemsSource = _destinations;
        RunningDestinationList.ItemsSource = _runningDestinations;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeForCurrentDpi(new SizeInt32(720, 320));

        try { SystemBackdrop = new MicaBackdrop(); } catch { }
        ConfigureNativeWindowChrome();

        ApplySavedTheme();
        _progressTimer.Tick += ProgressTimer_Tick;
        Closed += MainWindow_Closed;
        AppWindow.Closing += MainWindow_Closing;
        TryLoadLaunchSource();
    }

    private void ResizeForCurrentDpi(SizeInt32 size)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ConfigureNativeWindowChrome()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(24, 128, 128, 128);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 128, 128, 128);
        }
        catch { }
    }

    private void ApplySavedTheme()
    {
        var settings = SettingsStore.Load();
        Root.RequestedTheme = settings.Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
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
                Title = "Selecciona el archivo que quieres copiar",
                CommitButtonText = "Seleccionar",
            };
            var result = await picker.PickSingleFileAsync();
            if (result is not null) SourcePathBox.Text = result.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void PickSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id)
            {
                Title = "Selecciona la carpeta que quieres copiar",
                CommitButtonText = "Seleccionar",
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is not null) SourcePathBox.Text = result.Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void AddDestinations_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(AppWindow.Id)
            {
                Title = "Selecciona dónde quieres guardar la copia",
                CommitButtonText = "Agregar",
            };
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
        if (_job is not null || _preparationCancel is not null) return;
        try
        {
            ErrorBar.IsOpen = false;
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
            _preparationCancel = new CancellationTokenSource();
            _cancellationRequested = false;
            NewCopyButton.Visibility = Visibility.Collapsed;
            ResultDetailsButton.Visibility = Visibility.Collapsed;
            _lastResult = [];
            _lastDiagnostics = null;
            _runningDestinations.Clear();
            RunningDestinationScroll.Visibility = Visibility.Collapsed;
            StartButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            StatusText.Text = "Preparando la copia…";
            ShowRunningView();
            OperationIcon.Glyph = "\uE8A5";
            OperationTitleText.Text = "Preparando...";
            CurrentFileText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePathBox.Text));
            CurrentPathText.Text = SourcePathBox.Text;
            SpeedMetricText.Text = "0 MiB/s";
            RemainingMetricText.Text = "--:--:--";
            FilesMetricText.Text = "0/0";
            OverallProgressBar.Value = 0;
            OverallPercentText.Text = "0%";
            OverallDetailText.Text = "Preparando copia con verificación rápida...";
            PauseButtonText.Text = "Pausar";
            PauseIcon.Glyph = "\uE769";
            _copyStartedAt = DateTimeOffset.Now;
            _copyProgressRate.Reset();

            _job = await CopyEngine.StartAsync(plan, options, _preparationCancel.Token);
            RefreshProgress();
            if (_cancellationRequested) _job.RequestCancel();
            PauseButton.IsEnabled = !_cancellationRequested;
            CancelButton.IsEnabled = !_cancellationRequested;
            if (!_cancellationRequested)
            {
                StatusText.Text = plan.SkipSame ? "Comprobando archivos existentes…" : "Copiando…";
                OperationTitleText.Text = "Copiando...";
            }
            _progressTimer.Start();
            _ = ObserveJobCompletionAsync(_job);
        }
        catch (OperationCanceledException)
        {
            _job = null;
            _copyStartedAt = null;
            ShowPreparationView();
            SetEditingEnabled(true);
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            StatusText.Text = "Preparación cancelada";
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
        finally
        {
            _preparationCancel?.Dispose();
            _preparationCancel = null;
            if (_closeRequested && _job is null) Close();
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null) return;
        var paused = !_job.IsPaused;
        _job.SetPaused(paused);
        PauseButtonText.Text = paused ? "Continuar" : "Pausar";
        PauseIcon.Glyph = paused ? "\uE768" : "\uE769";
        if (paused)
        {
            OperationTitleText.Text = "Pausado";
            StatusText.Text = "Pausado";
            RefreshProgress();
            return;
        }

        var verifying = _job.Snapshot().Any(item => item.Phase == DestinationPhase.Verifying);
        OperationTitleText.Text = verifying ? "Comprobando integridad..." : "Copiando...";
        StatusText.Text = verifying ? "Verificando integridad…" : "Copiando…";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null && _preparationCancel is null) return;
        _cancellationRequested = true;
        _preparationCancel?.Cancel();
        _job?.RequestCancel();
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        OperationTitleText.Text = "Cancelando...";
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
                _lastResult = snapshots;
                _lastDiagnostics = observed.DiagnosticsSnapshot();
                var failed = snapshots.Count(item => item.Phase == DestinationPhase.Failed);
                var cancelled = snapshots.Any(item => item.Phase == DestinationPhase.Cancelled);
                var erroredFiles = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => sum + item.FilesErrored);
                var completedWithErrors = failed > 0 || erroredFiles > 0;
                var filesTotal = snapshots.Count == 0 ? 0UL : snapshots.Max(item => item.FilesTotal);
                var filesDone = snapshots.Count == 0 ? 0UL : snapshots
                    .Where(item => item.Phase is not DestinationPhase.Failed and not DestinationPhase.Cancelled)
                    .Select(item => item.FilesDone)
                    .DefaultIfEmpty(snapshots.Max(item => item.FilesDone))
                    .Min();
                var sourceBytes = snapshots.Count == 0 ? 0UL : snapshots[0].Total;
                var elapsed = _copyStartedAt is null ? TimeSpan.Zero : DateTimeOffset.Now - _copyStartedAt.Value;

                OperationTitleText.Text = cancelled
                    ? "Cancelado"
                    : completedWithErrors ? "Completado con errores" : "Completado";
                OperationIcon.Glyph = cancelled || completedWithErrors ? "\uE783" : "\uE73E";
                StatusText.Text = cancelled
                    ? "Copia cancelada"
                    : completedWithErrors ? "La copia terminó con algunos errores" : "Copia completada";
                CurrentFileText.Text = filesTotal == 0 ? "Sin archivos" : $"{filesDone}/{filesTotal} archivos";
                CurrentPathText.Text = $"{FormatBytes(sourceBytes)} · {FormatDuration(elapsed)}";
                SpeedMetricText.Text = "0.0 B/s";
                RemainingMetricText.Text = "00:00:00";
                PauseButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                await observed.DisposeAsync();
                _job = null;
                SetEditingEnabled(true);
                StartButton.IsEnabled = true;
                NewCopyButton.Visibility = Visibility.Visible;
                ResultDetailsButton.Visibility = Visibility.Visible;

                if (_closeRequested)
                {
                    Close();
                }
                else if (!cancelled && !completedWithErrors && ShutdownCheck.IsChecked == true)
                {
                    try { await OfferShutdownAsync(); }
                    catch (Exception ex) { ShowError(ex.Message); }
                }
            }
        }
    }

    private void ProgressTimer_Tick(object? sender, object e) => RefreshProgress();

    private void RefreshProgress()
    {
        if (_job is null) return;
        var snapshots = _job.Snapshot();
        RunningDestinationScroll.Visibility = snapshots.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (index >= _runningDestinations.Count)
                _runningDestinations.Add(new RunningDestinationRow(snapshots[index].Label));
            _runningDestinations[index].Update(snapshots[index]);
        }
        var paused = _job.IsPaused;
        var verifying = snapshots.Any(item => item.Phase == DestinationPhase.Verifying);
        double percent;
        if (verifying)
        {
            var verifyActive = snapshots
                .Where(item => item.Phase is not DestinationPhase.Failed and not DestinationPhase.Cancelled && item.VerifyBytesTotal > 0)
                .ToArray();
            var verifyTotal = verifyActive.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.VerifyBytesTotal));
            var verified = verifyActive.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => checked(sum + item.VerifiedBytes));
            percent = verifyTotal == 0 ? 100 : Math.Clamp(verified * 100.0 / verifyTotal, 0, 100);
            OverallDetailText.Text = $"Verificados {FormatBytes(verified)} de {FormatBytes(verifyTotal)}";
            var diagnostics = _job.DiagnosticsSnapshot();
            var speed = paused ? 0d : diagnostics.VerifyLogical5sBytesPerSecond;
            SpeedMetricText.Text = paused ? "0.0 B/s" : Throughput.Format(speed);
            // Logical throughput counts a source block once. Estimate with the
            // largest remaining branch rather than multiplying ETA by destinations.
            var remaining = verifyActive.Select(item => item.VerifyBytesTotal > item.VerifiedBytes
                ? item.VerifyBytesTotal - item.VerifiedBytes : 0UL).DefaultIfEmpty(0UL).Max();
            RemainingMetricText.Text = !paused && speed > 1
                ? FormatDuration(TimeSpan.FromSeconds(remaining / speed))
                : "--:--:--";
            if (!_job.IsPaused && !_cancellationRequested)
            {
                OperationTitleText.Text = "Comprobando integridad...";
                StatusText.Text = "Verificando integridad de los destinos…";
            }
        }
        else
        {
            var active = snapshots
                .Where(item => item.Phase is not DestinationPhase.Failed and not DestinationPhase.Cancelled)
                .ToArray();
            var total = snapshots.Select(item => item.Total).DefaultIfEmpty(0UL).Max();
            var written = active.Length == 0
                ? snapshots.Select(item => item.Written).DefaultIfEmpty(0UL).Max()
                : active.Min(item => item.Written);
            var speed = paused ? 0d : _copyProgressRate.Observe(written);
            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);
            OverallDetailText.Text = snapshots.Count >= 2
                ? $"Destino más lento: {FormatBytes(written)} de {FormatBytes(total)}"
                : $"{FormatBytes(written)} de {FormatBytes(total)}";
            SpeedMetricText.Text = paused ? "0.0 B/s" : Throughput.Format(speed);
            var remaining = total > written ? total - written : 0;
            RemainingMetricText.Text = !paused && speed > 1
                ? FormatDuration(TimeSpan.FromSeconds(remaining / speed))
                : "--:--:--";
            if (!_job.IsPaused && !_cancellationRequested && snapshots.Any(item => item.Phase == DestinationPhase.Copying))
            {
                OperationTitleText.Text = "Copiando...";
                StatusText.Text = $"Copiando a {snapshots.Count} destino{(snapshots.Count == 1 ? string.Empty : "s")}…";
            }
        }

        OverallProgressBar.Value = percent;
        OverallPercentText.Text = $"{percent:0}%";

        var activeFileSnapshots = snapshots
            .Where(item => item.Phase is not DestinationPhase.Failed and not DestinationPhase.Cancelled)
            .ToArray();
        var filesTotal = snapshots.Count == 0 ? 0UL : snapshots.Max(item => item.FilesTotal);
        var filesDone = activeFileSnapshots.Length == 0
            ? snapshots.Select(item => item.FilesDone).DefaultIfEmpty(0UL).Max()
            : activeFileSnapshots.Min(item => item.FilesDone);
        FilesMetricText.Text = $"{filesDone}/{filesTotal}";

        if (_copyStartedAt is not null)
            ElapsedText.Text = $"Tiempo transcurrido: {FormatDuration(DateTimeOffset.Now - _copyStartedAt.Value)}";

        var current = snapshots.Select(item => item.LastFile).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(current)) CurrentFileText.Text = Path.GetFileName(current);
    }

    private async void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null || _preparationCancel is not null) return;
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
            ShowPreparationView();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = new CopyProfile(
                CopyProfile.CurrentVersion,
                SourcePathBox.Text,
                _destinations.Select(item => item.Path).ToArray(),
                SkipSameCheck.IsChecked == true,
                KeepGoingCheck.IsChecked == true,
                ShutdownCheck.IsChecked == true,
                true);
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
        try { await ShowSettingsAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task ShowSettingsAsync()
    {
        var themeBox = new ComboBox { Header = "Tema", Width = 300 };
        themeBox.Items.Add(new ComboBoxItem { Content = "Sistema", Tag = "Default" });
        themeBox.Items.Add(new ComboBoxItem { Content = "Claro", Tag = "Light" });
        themeBox.Items.Add(new ComboBoxItem { Content = "Oscuro", Tag = "Dark" });
        themeBox.SelectedIndex = Root.RequestedTheme switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };

        var content = new StackPanel { Spacing = 12, Width = 320 };
        content.Children.Add(new TextBlock
        {
            Text = "Apariencia",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(themeBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Ajustes",
            PrimaryButtonText = "Aplicar",
            CloseButtonText = "Cerrar",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary ||
            themeBox.SelectedItem is not ComboBoxItem item)
            return;

        var theme = item.Tag?.ToString() switch
        {
            "Light" => ThemePreference.Light,
            "Dark" => ThemePreference.Dark,
            _ => ThemePreference.System,
        };
        Root.RequestedTheme = theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        SettingsStore.Save(new AppSettings(theme));
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        try { await ShowAboutAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task ShowAboutAsync()
    {
        var previousBackground = AboutMenuButton.Background;
        AboutMenuButton.Background = ResolveBrush("AccentFillColorSecondaryBrush");
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                PrimaryButtonText = "Cerrar",
                DefaultButton = ContentDialogButton.Primary,
            };

            var root = new Grid { Width = 470 };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var closeButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Content = new FontIcon { Glyph = "\uE711", FontSize = 13 },
            };
            ToolTipService.SetToolTip(closeButton, "Cerrar");
            closeButton.Click += (_, _) => dialog.Hide();
            root.Children.Add(closeButton);

            var header = new Grid { Margin = new Thickness(0, 14, 42, 16), ColumnSpacing = 16 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var logoTile = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(12),
                Child = new Image
                {
                    Width = 64,
                    Height = 64,
                    Stretch = Stretch.Uniform,
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/AppLogo.png")),
                },
            };
            header.Children.Add(logoTile);

            var brand = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(new TextBlock
            {
                Text = "RepartoCopier",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            brand.Children.Add(new TextBlock
            {
                Text = "Copias rápidas y seguras para Windows.",
                FontSize = 13,
                Opacity = 0.70,
            });
            Grid.SetColumn(brand, 1);
            header.Children.Add(brand);
            Grid.SetRow(header, 1);
            root.Children.Add(header);

            var version = typeof(MainWindow).Assembly.GetName().Version;
            var displayVersion = version is null
                ? "desconocida"
                : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = $"Versión {displayVersion}", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = "© 2026 ReinierTutoriales\nTodos los derechos reservados.", FontSize = 13, Opacity = 0.82 });
            body.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 8, 0, 8),
                Background = ResolveBrush("DividerStrokeColorDefaultBrush"),
            });
            body.Children.Add(new TextBlock { Text = "Gracias por usar RepartoCopier. ❤️", FontSize = 13 });
            body.Children.Add(new TextBlock { Text = "¡Dale ❤️ al proyecto en GitHub!", FontSize = 13 });

            var actions = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnSpacing = 10 };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var githubButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height = 40,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Image
                        {
                            Width = 17,
                            Height = 17,
                            Source = new SvgImageSource { UriSource = new Uri("ms-appx:///Assets/GitHubMark.svg") },
                        },
                        new TextBlock { Text = "Ver en GitHub" },
                    },
                },
            };
            githubButton.Click += (_, _) => OpenExternalUrl(ProjectUrl);
            actions.Children.Add(githubButton);

            var licenseButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height = 40,
                Content = "Licencias de terceros",
            };
            licenseButton.Click += (_, _) => OpenExternalUrl(LicenseUrl);
            Grid.SetColumn(licenseButton, 1);
            actions.Children.Add(licenseButton);
            body.Children.Add(actions);

            Grid.SetRow(body, 2);
            root.Children.Add(body);
            dialog.Content = root;
            await ShowDialogAsync(dialog);
        }
        finally
        {
            AboutMenuButton.Background = previousBackground;
        }
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null && _preparationCancel is null) Close();
        else BeginSafeClose();
    }

    private async Task OfferShutdownAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Copia completada",
            Content = "El equipo se apagará en 60 segundos. Puedes cancelar el apagado desde Windows con shutdown /a.",
            PrimaryButtonText = "Apagar",
            CloseButtonText = "No apagar",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
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
        PickSourceFileButton.IsEnabled = enabled;
        PickSourceFolderButton.IsEnabled = enabled;
        AddDestinationsButton.IsEnabled = enabled;
        ClearDestinationsButton.IsEnabled = enabled;
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

    private void MainWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_job is null && _preparationCancel is null) return;
        args.Cancel = true;
        BeginSafeClose();
    }

    private void BeginSafeClose()
    {
        _closeRequested = true;
        _cancellationRequested = true;
        _preparationCancel?.Cancel();
        _job?.RequestCancel();
        PauseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        AppMenuButton.IsEnabled = false;
        StatusText.Text = "Cerrando de forma segura…";
    }

    private void NewCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null || _preparationCancel is not null) return;
        ShowPreparationView();
        StatusText.Text = "Listo";
    }

    private async void ResultDetails_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var destinations = _lastResult;
            var diagnostics = _lastDiagnostics;
            var startedAt = _copyStartedAt;
            var details = string.Join(Environment.NewLine + Environment.NewLine, destinations.Select(item =>
                $"{item.Label}\nEstado: {FormatPhase(item.Phase)} · Archivos: {item.FilesDone}/{item.FilesTotal} · Errores: {item.FilesErrored}" +
                (string.IsNullOrWhiteSpace(item.Error) ? string.Empty : $"\n{item.Error}")));
            var result = await ShowDialogAsync(new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Resultado por destino",
                PrimaryButtonText = "Guardar diagnóstico",
                IsPrimaryButtonEnabled = diagnostics is not null,
                CloseButtonText = "Cerrar",
                Content = new ScrollViewer
                {
                    MaxHeight = 360,
                    Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
            });
            if (result == ContentDialogResult.Primary && diagnostics is not null)
            {
                var picker = new FileSavePicker(AppWindow.Id)
                {
                    Title = "Guardar diagnóstico de copia",
                    SuggestedFileName = "diagnostico-copia",
                    DefaultFileExtension = ".json",
                    FileTypeChoices = { { "Diagnóstico JSON", new List<string> { ".json" } } },
                };
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;
                var options = new JsonSerializerOptions { WriteIndented = true };
                options.Converters.Add(new JsonStringEnumConverter());
                var json = JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    ApplicationVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                    StartedAt = startedAt,
                    Diagnostics = diagnostics,
                    Destinations = destinations,
                    MeasurementNotes = new[]
                    {
                        "Durations are TimeSpan strings; byte rates use bytes per second.",
                        "BufferWaitTime measures the complete RentAsync call, including immediate rentals.",
                        "SourceReadTime includes source scheduler acquisition when a device is shared.",
                        "WriteTime and flush times aggregate concurrent destinations; they are not COPY wall time.",
                        "This final snapshot does not record a time series or individual destination completion times.",
                    },
                }, options);
                await File.WriteAllTextAsync(file.Path, json);
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private static string FormatPhase(DestinationPhase phase) => phase switch
    {
        DestinationPhase.Done => "Completado",
        DestinationPhase.Failed => "Fallido",
        DestinationPhase.Cancelled => "Cancelado",
        _ => phase.ToString(),
    };

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (_closeRequested) return ContentDialogResult.None;
            return await dialog.ShowAsync();
        }
        finally { _dialogGate.Release(); }
    }

    private sealed class LogicalProgressRate
    {
        private long _lastTimestamp;
        private ulong _lastBytes;
        private double _smoothedBytesPerSecond;

        internal void Reset()
        {
            _lastTimestamp = 0;
            _lastBytes = 0;
            _smoothedBytesPerSecond = 0;
        }

        internal double Observe(ulong bytes)
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastTimestamp == 0 || bytes < _lastBytes)
            {
                _lastTimestamp = now;
                _lastBytes = bytes;
                _smoothedBytesPerSecond = 0;
                return 0;
            }
            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            if (elapsed < 0.15) return _smoothedBytesPerSecond;
            var delta = bytes - _lastBytes;
            var instant = delta / elapsed;
            _lastTimestamp = now;
            _lastBytes = bytes;
            if (delta == 0)
            {
                _smoothedBytesPerSecond *= 0.82;
                if (_smoothedBytesPerSecond < 1024) _smoothedBytesPerSecond = 0;
                return _smoothedBytesPerSecond;
            }
            _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0 ? instant : (_smoothedBytesPerSecond * 0.70) + (instant * 0.30);
            return _smoothedBytesPerSecond;
        }
    }

    private static string FormatDestinationCount(int count) =>
        count == 1 ? "1 destino" : $"{count} destinos";

    private static string FormatDuration(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private static Brush ResolveBrush(string resourceKey) =>
        (Brush)Application.Current.Resources[resourceKey];

    public sealed record DestinationRow(string Path);

    public sealed class RunningDestinationRow(string path) : INotifyPropertyChanged
    {
        public string Label { get; } = path;
        public string Progress { get; private set; } = "Preparando";
        public string Detail { get; private set; } = path;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(DestinationSnapshot snapshot)
        {
            var progress = DestinationProgressText.Format(snapshot);
            var detail = $"{Label}\n{progress}\nCopiados: {FormatBytes(snapshot.Written)} de {FormatBytes(snapshot.Total)}";
            if (snapshot.VerifyBytesTotal > 0)
                detail += $"\nVerificados: {FormatBytes(snapshot.VerifiedBytes)} de {FormatBytes(snapshot.VerifyBytesTotal)}";
            if (!string.IsNullOrWhiteSpace(snapshot.Error)) detail += $"\n{snapshot.Error}";
            if (Progress != progress)
            {
                Progress = progress;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
            }
            if (Detail != detail)
            {
                Detail = detail;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
            }
        }
    }

}
