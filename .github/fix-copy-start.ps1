$ErrorActionPreference = 'Stop'

$corePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$core = Get-Content $corePath -Raw
$anchor = '    private static PreparedCopy Preflight(CopyPlan plan)'
if (-not $core.Contains($anchor)) { throw 'CopyEngine preflight anchor not found' }
if (-not $core.Contains('public static async Task<CopyJob> StartAsync')) {
$method = @'
    public static async Task<CopyJob> StartAsync(
        CopyPlan plan,
        CopyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CopyOptions(
            Verify: true,
            SkipSame: plan.SkipSame,
            KeepGoing: plan.KeepGoing);

        var prepared = await Task.Run(() => Preflight(plan), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var progress = prepared.DestinationRoots
            .Select(root => new DestinationProgress(root, prepared.TotalBytes))
            .ToArray();
        var job = new CopyJob(progress);
        job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
        return job;
    }

'@
    $core = $core.Replace($anchor, $method + $anchor)
    Set-Content $corePath $core -NoNewline
}

$uiPath = 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
$ui = Get-Content $uiPath -Raw
$ui = $ui.Replace('private void Start_Click(object sender, RoutedEventArgs e)', 'private async void Start_Click(object sender, RoutedEventArgs e)')

$old = @'
            _job = CopyEngine.Start(plan, options);
            SetEditingEnabled(false);
            StartButton.IsEnabled = false;
            PauseButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            PauseButton.Content = "Pausar";
            StatusText.Text = "Preparando copia…";
'@
$new = @'
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
'@
if (-not $ui.Contains($old)) { throw 'MainWindow start block anchor not found' }
$ui = $ui.Replace($old, $new)

$oldCatch = @'
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void Pause_Click
'@
$newCatch = @'
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

    private void Pause_Click
'@
if (-not $ui.Contains($oldCatch)) { throw 'MainWindow start catch anchor not found' }
$ui = $ui.Replace($oldCatch, $newCatch)
Set-Content $uiPath $ui -NoNewline

$xamlPath = 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
$xaml = Get-Content $xamlPath -Raw
$oldCheck = '<CheckBox x:Name="SkipSameCheck" Content="Omitir archivos iguales" IsChecked="True" />'
$newCheck = '<CheckBox x:Name="SkipSameCheck" Content="Omitir archivos iguales" IsChecked="False" />'
if (-not $xaml.Contains($oldCheck)) { throw 'SkipSame default anchor not found' }
$xaml = $xaml.Replace($oldCheck, $newCheck)
Set-Content $xamlPath $xaml -NoNewline
