using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using RepartoCopier.Core;
using Windows.Graphics;

namespace RepartoCopier.WinUI;

public sealed partial class MainWindow : Window
{

    private readonly ObservableCollection<DestinationRow> _destinations = [];
    private readonly ObservableCollection<RunningDestinationRow> _runningDestinations = [];
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private CopyJob? _job;
    private CancellationTokenSource? _preparationCancel;
    private bool _closeRequested;
    private bool _cancellationRequested;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly CancellationTokenSource _windowLifetime = new();
    private IReadOnlyList<DestinationSnapshot> _lastResult = [];
    private CopyDiagnosticsSnapshot? _lastDiagnostics;
    private DateTimeOffset? _copyStartedAt;
    private bool _verificationRequested;
    private bool? _independentSourceReadsUsed;
    private readonly LogicalProgressRate _copyProgressRate = new();
    // Stopwatch tick of the last resume, 0 if none. The engine's verify rate averages over 5 s, pause included.
    private long _resumedTick;
    private static readonly TimeSpan VerifyRateSettle = TimeSpan.FromSeconds(5);
    private readonly TaskbarProgress _taskbar;
    private readonly CompletionNotifier _notifier;
    private CopyPowerRequest? _power;

    public MainWindow()
    {
        InitializeComponent();
        _taskbar = new TaskbarProgress(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _notifier = new CompletionNotifier(this);
        DestinationList.ItemsSource = _destinations;
        RunningDestinationList.ItemsSource = _runningDestinations;
        _destinations.CollectionChanged += (_, _) => UpdateDestinationSummary();
        UpdateDestinationSummary();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ResizeForCurrentDpi(new SizeInt32(DefaultWidth, DefaultHeight));

        NativeWindowMaterial.Apply(this, Root);
        // Alt+Tab, the taskbar and the title bar show the app icon, not the default executable icon.
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.ico")); } catch { }

        ApplySavedTheme();
        // Caption buttons follow the theme the content actually renders in (app setting or Windows).
        AppDialog.StyleCaptionButtons(AppWindow, Root.ActualTheme);
        Root.ActualThemeChanged += (sender, _) => AppDialog.StyleCaptionButtons(AppWindow, sender.ActualTheme);
        _progressTimer.Tick += ProgressTimer_Tick;
        Closed += MainWindow_Closed;
        AppWindow.Closing += MainWindow_Closing;
        AppWindow.Changed += (_, change) =>
        {
            if (change.DidPositionChange || change.DidSizeChange) UpdateWindowMinimum();
        };
        UpdateWindowMinimum();
        TryLoadLaunchSource();
    }

    private const int DefaultWidth = 760;
    private const int DefaultHeight = 540;
    private const double WideLayoutMinWidth = 640;
    private bool? _wideLayout;

    // Responsive layout in code: AdaptiveTrigger is not evaluated reliably for Window content without a Page.
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var wide = width >= WideLayoutMinWidth;
        // Height-only changes and individual resize pixels do not change the layout mode.
        if (_wideLayout == wide) return;
        _wideLayout = wide;
        PreparationHeader.RowSpacing = wide ? 0 : 8;
        RunningActions.RowSpacing = wide ? 0 : 8;
        ContentHost.Padding = wide ? new Thickness(16, 8, 16, 16) : new Thickness(12, 8, 12, 12);
        var labels = wide ? Visibility.Visible : Visibility.Collapsed;
        PickSourceFileLabel.Visibility = labels;
        PickSourceFolderLabel.Visibility = labels;
        SubtitleText.Visibility = labels;
        Microsoft.UI.Xaml.Controls.Grid.SetRow(StartButton, wide ? 0 : 1);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(StartButton, wide ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(StartButton, wide ? 1 : 2);
        StartButton.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        Microsoft.UI.Xaml.Controls.Grid.SetRow(ShutdownCheck, wide ? 0 : 1);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(ShutdownCheck, wide ? 2 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(ShutdownCheck, wide ? 1 : 3);
        ShutdownCheck.HorizontalAlignment = wide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    private void UpdateDestinationSummary()
    {
        DestinationCountText.Text = FormatDestinationCount(_destinations.Count);
        DestinationsEmptyText.Visibility = _destinations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DestinationListHost.Visibility = _destinations.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResizeForCurrentDpi(SizeInt32 size)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var frameWidth = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
        var frameHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        var client = WindowGeometry.Client(size.Width, size.Height, GetDpiForWindow(hwnd),
            area.Width, area.Height, frameWidth, frameHeight);
        AppWindow.ResizeClient(new SizeInt32(client.Width, client.Height));
    }

    private void UpdateWindowMinimum()
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter) return;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var minimum = WindowGeometry.Minimum(GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)),
            area.Width, area.Height, Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width),
            Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height));
        if (presenter.PreferredMinimumWidth != minimum.Width) presenter.PreferredMinimumWidth = minimum.Width;
        if (presenter.PreferredMinimumHeight != minimum.Height) presenter.PreferredMinimumHeight = minimum.Height;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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

    // Order matters: profile, then --source (overrides the profile's source), then --dest (appended to the profile's destinations).
    private void TryLoadLaunchSource()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length == 2 && !args[1].StartsWith("-", StringComparison.Ordinal))
        {
            SourcePathBox.Text = args[1];
            return;
        }

        string? profilePath = null;
        string? source = null;
        var destinations = new List<string>();
        for (var index = 1; index < args.Length - 1; index++)
        {
            switch (args[index])
            {
                case "--profile": profilePath = args[++index]; break;
                case "--source": source = args[++index]; break;
                case "--dest": destinations.Add(args[++index]); break;
            }
        }

        if (profilePath is not null)
        {
            try { ApplyProfile(CopyProfileSerializer.Deserialize(File.ReadAllText(profilePath))); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        if (source is not null) SourcePathBox.Text = source;
        var missing = destinations.Where(path => !Directory.Exists(path)).ToArray();
        AddDestinationPaths(destinations.Except(missing).ToArray());
        if (missing.Length > 0)
            ShowWarning("Destinos no encontrados", $"Se omitieron: {string.Join(", ", missing)}");
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
            AddDestinationPaths(results.Select(result => result.Path).ToArray());
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void AddDestinationPaths(string[] paths)
    {
        var existing = _destinations.Select(item => item.Path).ToList();
        var previous = existing.Count;
        CopyPlan.AppendUniqueDestinations(SourcePathBox.Text, existing, paths);
        // Only the new folders are added; existing chips are not rebuilt.
        for (var index = previous; index < existing.Count; index++) _destinations.Add(new DestinationRow(existing[index]));
        if (existing.Count >= CopyPlan.MaxDestinations && paths.Length > existing.Count - previous)
            ShowWarning("Límite de destinos", $"Se alcanzó el máximo de {CopyPlan.MaxDestinations} destinos; las demás carpetas no se agregaron.");
    }

    // Drag and drop from Explorer: a file or folder onto the source card, folders onto the destinations card.
    private void DropCard_DragOver(object sender, DragEventArgs e)
    {
        var accepts = _job is null && _preparationCancel is null &&
                      e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
        e.AcceptedOperation = accepts
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        if (accepts && e.DragUIOverride is { } overlay)
            overlay.Caption = ReferenceEquals(sender, SourceCard) ? "Usar como origen" : "Agregar como destino";
    }

    private async void SourceCard_Drop(object sender, DragEventArgs e)
    {
        if (_job is not null || _preparationCancel is not null) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0) SourcePathBox.Text = items[0].Path;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void DestinationsCard_Drop(object sender, DragEventArgs e)
    {
        if (_job is not null || _preparationCancel is not null) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            AddDestinationPaths(items.OfType<Windows.Storage.StorageFolder>().Select(folder => folder.Path).ToArray());
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void RemoveDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        var item = _destinations.FirstOrDefault(row => WindowsPath.SamePath(row.Path, path));
        if (item is not null) _destinations.Remove(item);
    }

    private void ClearDestinations_Click(object sender, RoutedEventArgs e) => _destinations.Clear();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null || _preparationCancel is not null) return;
        try
        {
            ErrorBar.IsOpen = false;
            var plan = CopyPlan.Create(
                SourcePathBox.Text,
                _destinations.Select(item => item.Path),
                // Fixed product policy: existing files with the same size and date are skipped without
                // reading them; the rest are replaced. The engine reads the source once per destination.
                ExistingFilePolicy.ReplaceMetadataDifferent,
                KeepGoingCheck.IsChecked == true);
            var options = new CopyOptions(
                Verify: VerifyCheck.IsChecked == true,
                KeepGoing: plan.KeepGoing);

            SetEditingEnabled(false);
            _verificationRequested = options.Verify;
            _independentSourceReadsUsed = null;
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
            SetOperationState(OperationState.Active, "\uE8A5");
            OperationTitleText.Text = "Preparando…";
            CurrentFileText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePathBox.Text));
            CurrentPathText.Text = SourcePathBox.Text;
            SpeedMetricText.Text = Throughput.Format(0);
            RemainingMetricText.Text = "--:--:--";
            FilesMetricText.Text = "0/0";
            OverallProgressBar.Value = 0;
            OverallPercentText.Text = "0%";
            OverallDetailText.Text = options.Verify
                ? "Preparando copia con verificación completa…"
                : "Preparando copia sin verificación final…";
            SetPauseButton(paused: false);
            _taskbar.Set(TaskbarState.Indeterminate);
            _power ??= CopyPowerRequest.Begin();
            _copyStartedAt = DateTimeOffset.Now;
            _copyProgressRate.Reset();
            _resumedTick = 0;

            _job = await CopyEngine.StartAsync(plan, options, _preparationCancel.Token);
            RefreshProgress();
            if (_cancellationRequested) _job.RequestCancel();
            PauseButton.IsEnabled = !_cancellationRequested;
            CancelButton.IsEnabled = !_cancellationRequested;
            if (!_cancellationRequested)
            {
                StatusText.Text = "Copiando…";
                OperationTitleText.Text = PhaseTitle(verifying: false);
            }
            _progressTimer.Start();
            _ = ObserveJobCompletionAsync(_job);
        }
        catch (OperationCanceledException)
        {
            _job = null;
            _copyStartedAt = null;
            _taskbar.Set(TaskbarState.None);
            ReleasePower();
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
            _taskbar.Set(TaskbarState.None);
            ReleasePower();
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
        SetPauseButton(paused);
        if (paused)
        {
            StatusText.Text = "Pausado";
        }
        else
        {
            // Restore the running state now; the next refresh only refines it.
            var verifying = _job.Snapshot().Any(item => item.Phase == DestinationPhase.Verifying);
            SetOperationState(OperationState.Active, verifying ? "\uE9D5" : "\uE8A5");
            _resumedTick = Stopwatch.GetTimestamp();
            _copyProgressRate.Reanchor();
            OperationTitleText.Text = PhaseTitle(verifying);
            StatusText.Text = verifying ? "Verificando integridad de los destinos…" : "Copiando…";
        }
        RefreshProgress();
    }

    private void SetPauseButton(bool paused)
    {
        var label = paused ? "Continuar" : "Pausar";
        PauseButtonText.Text = label;
        PauseIcon.Glyph = paused ? "\uE768" : "\uE769";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseButton, label);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_job is null && _preparationCancel is null) return;
        _cancellationRequested = true;
        _preparationCancel?.Cancel();
        _job?.RequestCancel();
        CancelButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        OperationTitleText.Text = "Cancelando…";
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
                _independentSourceReadsUsed = observed.IndependentSourceReads;
                var failed = snapshots.Count(item => item.Phase == DestinationPhase.Failed);
                var cancelled = snapshots.Any(item => item.Phase == DestinationPhase.Cancelled);
                var erroredFiles = snapshots.Aggregate<DestinationSnapshot, ulong>(0, (sum, item) => sum + item.FilesErrored);
                var completedWithErrors = failed > 0 || erroredFiles > 0;
                var hasUnverifiedSkips = snapshots.Any(item => item.FilesSkipped > 0);
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
                    : completedWithErrors ? "Completado con errores"
                    : _verificationRequested && hasUnverifiedSkips ? "Terminado · omitidos sin verificar"
                    : _verificationRequested ? "Copia y verificación terminadas" : "Copiado · sin verificación";
                if (cancelled) SetOperationState(OperationState.Caution, "\uE711");
                else if (completedWithErrors) SetOperationState(OperationState.Critical);
                else SetOperationState(OperationState.Success);
                StatusText.Text = cancelled
                    ? "Copia cancelada"
                    : completedWithErrors ? "La copia terminó con algunos errores"
                    : _verificationRequested && hasUnverifiedSkips ? "Terminada; los omitidos no se verificaron al finalizar"
                    : _verificationRequested ? "Copia y verificación completadas" : "Copia completada sin verificación final";
                CurrentFileText.Text = filesTotal == 0 ? "Sin archivos" : $"{filesDone}/{filesTotal} archivos";
                CurrentPathText.Text = $"{FormatBytes(sourceBytes)} · {FormatDuration(elapsed)}";
                SpeedMetricText.Text = Throughput.Format(0);
                RemainingMetricText.Text = "00:00:00";
                PauseButton.IsEnabled = false;
                CancelButton.IsEnabled = false;
                // A failed result stays red on the taskbar until the next copy; success or cancel clears it.
                _taskbar.Set(completedWithErrors && !cancelled ? TaskbarState.Error : TaskbarState.None, 100);
                Announce(StatusText.Text);
                if (!cancelled) _notifier.ShowIfInBackground(OperationTitleText.Text,
                    $"{FormatDestinationCount(snapshots.Count)} · {FormatBytes(sourceBytes)} · {FormatDuration(elapsed)}");
                _job = null;
                // Disposing waits for the engine's own cleanup; it must never strand the window in "running".
                try { await observed.DisposeAsync(); }
                catch (Exception ex) { ShowError(ex.Message); }
                // The copy's pinned buffers are garbage now; return their memory to Windows without waiting
                // for the next natural full collection.
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
                SetEditingEnabled(true);
                StartButton.IsEnabled = true;
                NewCopyButton.Visibility = Visibility.Visible;
                ResultDetailsButton.Visibility = Visibility.Visible;

                if (_closeRequested)
                {
                    ReleasePower();
                    Close();
                }
                else if (!cancelled && !completedWithErrors && ShutdownCheck.IsChecked == true)
                {
                    // Keep the system awake through the shutdown countdown, then let it go.
                    try { await OfferShutdownAsync(); }
                    catch (Exception ex) { ShowError(ex.Message); }
                    finally { ReleasePower(); }
                }
                else
                {
                    ReleasePower();
                }
            }
        }
    }

    private static readonly TimeSpan ForegroundRefresh = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinimizedRefresh = TimeSpan.FromSeconds(2);

    private void ProgressTimer_Tick(object? sender, object e)
    {
        // Minimized, only the taskbar button shows progress: refresh it every 2 s instead of 4 times a second.
        var minimized = AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
            { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
        var interval = minimized ? MinimizedRefresh : ForegroundRefresh;
        if (_progressTimer.Interval != interval) _progressTimer.Interval = interval;
        RefreshProgress();
    }

    private void ReleasePower()
    {
        _power?.Dispose();
        _power = null;
    }

    private void RefreshProgress()
    {
        if (_job is null) return;
        var snapshots = _job.Snapshot();
        var paused = _job.IsPaused;
        RunningDestinationScroll.Visibility = snapshots.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;

        // One pass over the snapshots per tick: the timer runs four times per second for the whole copy.
        var verifying = false;
        var anyCopying = false;
        ulong total = 0, minWritten = ulong.MaxValue, maxWritten = 0;
        ulong verifyTotal = 0, verified = 0, verifyRemaining = 0;
        ulong filesTotal = 0, minFilesDone = ulong.MaxValue, maxFilesDone = 0;
        string? current = null;
        for (var index = 0; index < snapshots.Count; index++)
        {
            var item = snapshots[index];
            if (index >= _runningDestinations.Count)
                _runningDestinations.Add(new RunningDestinationRow(item.Label));
            _runningDestinations[index].Update(item, paused);

            verifying |= item.Phase == DestinationPhase.Verifying;
            anyCopying |= item.Phase == DestinationPhase.Copying;
            total = Math.Max(total, item.Total);
            filesTotal = Math.Max(filesTotal, item.FilesTotal);
            maxWritten = Math.Max(maxWritten, item.Written);
            maxFilesDone = Math.Max(maxFilesDone, item.FilesDone);
            current ??= string.IsNullOrWhiteSpace(item.LastFile) ? null : item.LastFile;
            if (item.Phase is DestinationPhase.Failed or DestinationPhase.Cancelled) continue;
            minWritten = Math.Min(minWritten, item.Written);
            minFilesDone = Math.Min(minFilesDone, item.FilesDone);
            if (item.VerifyBytesTotal > 0)
            {
                verifyTotal += item.VerifyBytesTotal;
                verified += item.VerifiedBytes;
                verifyRemaining = Math.Max(verifyRemaining,
                    item.VerifyBytesTotal > item.VerifiedBytes ? item.VerifyBytesTotal - item.VerifiedBytes : 0UL);
            }
        }
        // The slowest healthy destination decides completion; if none is healthy, show the furthest one.
        var written = minWritten == ulong.MaxValue ? maxWritten : minWritten;
        var filesDone = minFilesDone == ulong.MaxValue ? maxFilesDone : minFilesDone;

        double percent;
        if (verifying)
        {
            percent = verifyTotal == 0 ? 100 : Math.Clamp(verified * 100.0 / verifyTotal, 0, 100);
            SetText(OverallDetailText, $"Verificados {FormatBytes(verified)} de {FormatBytes(verifyTotal)}");
            // Logical throughput counts a source block once. Estimate with the
            // largest remaining branch rather than multiplying ETA by destinations.
            var speed = paused ? 0d : _job.VerifyBytesPerSecond;
            SetText(SpeedMetricText, Throughput.Format(speed));
            var settling = _resumedTick != 0 && Stopwatch.GetElapsedTime(_resumedTick) < VerifyRateSettle;
            SetText(RemainingMetricText, speed > 1 && !settling ? FormatDuration(TimeSpan.FromSeconds(verifyRemaining / speed)) : "--:--:--");
            if (!paused && !_cancellationRequested)
            {
                SetOperationState(OperationState.Active, "\uE9D5");
                SetText(OperationTitleText, PhaseTitle(verifying: true));
                SetText(StatusText, "Verificando integridad de los destinos…");
            }
        }
        else
        {
            var speed = paused ? 0d : _copyProgressRate.Observe(written);
            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);
            SetText(OverallDetailText, (snapshots.Count >= 2
                ? $"Destino más lento: {FormatBytes(written)} de {FormatBytes(total)}"
                : $"{FormatBytes(written)} de {FormatBytes(total)}") + ReadModeSuffix());
            SetText(SpeedMetricText, Throughput.Format(speed));
            SetText(RemainingMetricText, speed > 1 ? FormatDuration(TimeSpan.FromSeconds((total - Math.Min(total, written)) / speed)) : "--:--:--");
            if (!paused && !_cancellationRequested && anyCopying)
            {
                SetOperationState(OperationState.Active, "\uE8A5");
                SetText(OperationTitleText, PhaseTitle(verifying: false));
                SetText(StatusText, $"Copiando a {snapshots.Count} destino{(snapshots.Count == 1 ? string.Empty : "s")}…");
            }
        }
        if (paused && !_cancellationRequested)
        {
            SetOperationState(OperationState.Caution, "\uE769");
            SetText(OperationTitleText, "Pausado");
        }

        OverallProgressBar.Value = percent;
        OverallProgressBar.ShowPaused = paused;
        _taskbar.Set(paused ? TaskbarState.Paused : TaskbarState.Normal, percent);
        SetText(OverallPercentText, $"{DestinationProgressText.FloorPercent(percent)}%");
        SetText(FilesMetricText, $"{filesDone}/{filesTotal}");
        if (_copyStartedAt is not null)
            SetText(ElapsedText, $"Tiempo transcurrido: {FormatDuration(DateTimeOffset.Now - _copyStartedAt.Value)}");
        if (current is not null) SetText(CurrentFileText, Path.GetFileName(current));
    }

    // Verification is one global pass after every copy, so the phase is global too.
    private string PhaseTitle(bool verifying) =>
        verifying ? "Verificación · 2/2" : _verificationRequested ? "Copia · 1/2" : "Copia";

    private string ReadModeSuffix() => _job?.IndependentSourceReads switch
    {
        true => " · lectura independiente",
        false => " · lectura conjunta",
        null => string.Empty,
    };

    // Four refreshes per second mostly repeat the same strings; skip the property change (and its layout).
    private static void SetText(Microsoft.UI.Xaml.Controls.TextBlock target, string value)
    {
        if (!string.Equals(target.Text, value, StringComparison.Ordinal)) target.Text = value;
    }

    // Narrator reads the final state once, without the four-per-second progress noise.
    private void Announce(string message)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(StatusText)
                   ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(StatusText);
        peer?.RaiseNotificationEvent(
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
            message, "RepartoCopierResult");
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
            ApplyProfile(CopyProfileSerializer.Deserialize(await File.ReadAllTextAsync(result.Path)));
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ApplyProfile(CopyProfile profile)
    {
        SourcePathBox.Text = profile.Source;
        _destinations.Clear();
        foreach (var path in profile.Destinations) _destinations.Add(new DestinationRow(path));
        KeepGoingCheck.IsChecked = profile.ContinueOnError;
        VerifyCheck.IsChecked = profile.VerifyAfterCopy;
        ShutdownCheck.IsChecked = profile.ShutdownWhenFinished;
        StatusText.Text = "Configuración cargada";
        ShowPreparationView();
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = new CopyProfile(
                CopyProfile.CurrentVersion,
                SourcePathBox.Text,
                _destinations.Select(item => item.Path).ToArray(),
                KeepGoingCheck.IsChecked == true,
                ShutdownCheck.IsChecked == true,
                VerifyCheck.IsChecked == true);
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
        var selected = Root.RequestedTheme switch
        {
            ElementTheme.Light => ThemePreference.Light,
            ElementTheme.Dark => ThemePreference.Dark,
            _ => ThemePreference.System,
        };
        var chosen = await ShowDialogAsync(() => AppDialogs.SettingsAsync(this, selected, _windowLifetime.Token));
        if (chosen is not { } theme) return;
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
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var displayVersion = version is null ? "desconocida"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        await ShowDialogAsync(async () =>
        {
            await AppDialogs.AboutAsync(this, displayVersion, OpenExternalUrl, _windowLifetime.Token);
            return true;
        });
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
        // The dialog already gave a cancellable 60 s countdown, so Windows shuts down right away.
        if (await ShowDialogAsync(() => AppDialogs.ShutdownAsync(this, _windowLifetime.Token)))
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
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
        DestinationListHost.IsEnabled = enabled;
        KeepGoingCheck.IsEnabled = enabled;
        VerifyCheck.IsEnabled = enabled;
    }

    private void ShowError(string message)
    {
        ShowInfoBar(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, "No se pudo completar", message);
        StatusText.Text = "Ocurrió un error";
    }

    private void ShowWarning(string title, string message) =>
        ShowInfoBar(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, title, message);

    private void ShowInfoBar(Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string title, string message)
    {
        ErrorBar.Severity = severity;
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        // The bar sits at the top of the scrolling content: bring it into view.
        MainContentScroll.ChangeView(null, 0, null);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _windowLifetime.Cancel();
        _progressTimer.Stop();
        _job?.RequestCancel();
        _notifier.Unregister();
        ReleasePower();
    }

    private void MainWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_job is null && _preparationCancel is null) return;
        if (_closeRequested)
        {
            // Second close while a safe close is still waiting (for example on a disk that stopped
            // responding): leave now. State is crash-safe: unfinished files stay as removable .part files.
            Environment.Exit(0);
            return;
        }
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
        StatusText.Text = "Cerrando de forma segura… (cierra de nuevo para forzar)";
    }

    private void NewCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_job is not null || _preparationCancel is not null) return;
        _taskbar.Set(TaskbarState.None);
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
            var verificationRequested = _verificationRequested;
            var save = await ShowDialogAsync(() => AppDialogs.ResultsAsync(this, destinations,
                verificationRequested, diagnostics is not null, _windowLifetime.Token));
            if (save && diagnostics is not null)
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
                var assembly = typeof(MainWindow).Assembly;
                var json = DiagnosticsExport.Serialize(DiagnosticsExport.Create(
                    assembly.GetName().Version?.ToString(),
                    BuildInfo.InformationalVersion(assembly),
                    startedAt,
                    diagnostics,
                    destinations,
                    verificationRequested,
                    _independentSourceReadsUsed));
                await File.WriteAllTextAsync(file.Path, json);
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // One dialog at a time; closing the main window cancels the open one.
    private async Task<T> ShowDialogAsync<T>(Func<Task<T>> show)
    {
        await _dialogGate.WaitAsync(_windowLifetime.Token);
        try
        {
            _windowLifetime.Token.ThrowIfCancellationRequested();
            return await show();
        }
        finally { _dialogGate.Release(); }
    }

    private sealed class LogicalProgressRate
    {
        private long _lastTimestamp;
        private ulong _lastBytes;
        private double _smoothedBytesPerSecond;
        private bool _reanchor;

        // Next sample starts a new time and byte base, keeping the average: pause time is not copy time.
        internal void Reanchor() => _reanchor = true;

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
            if (_reanchor)
            {
                _reanchor = false;
                _lastTimestamp = now;
                _lastBytes = bytes;
                return _smoothedBytesPerSecond;
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

    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB"];

    internal static string FormatBytes(ulong bytes)
    {
        var units = ByteUnits;
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private enum OperationState { Active, Success, Critical, Caution }

    // Each state is its own XAML layer bound to a theme brush, so Light/Dark switches recolor it for free.
    private void SetOperationState(OperationState state, string? glyph = null)
    {
        StatusActiveIcon.Visibility = state == OperationState.Active ? Visibility.Visible : Visibility.Collapsed;
        StatusSuccessIcon.Visibility = state == OperationState.Success ? Visibility.Visible : Visibility.Collapsed;
        StatusCriticalIcon.Visibility = state == OperationState.Critical ? Visibility.Visible : Visibility.Collapsed;
        StatusCautionIcon.Visibility = state == OperationState.Caution ? Visibility.Visible : Visibility.Collapsed;
        if (glyph is null) return;
        if (state == OperationState.Active && StatusActiveIcon.Glyph != glyph) StatusActiveIcon.Glyph = glyph;
        else if (state == OperationState.Caution && StatusCautionIcon.Glyph != glyph) StatusCautionIcon.Glyph = glyph;
    }
}
