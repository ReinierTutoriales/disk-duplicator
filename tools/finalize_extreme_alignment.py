from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "dotnet/RepartoCopier.Core/CopyEngine.cs"
TELEMETRY = ROOT / "dotnet/RepartoCopier.Core/CopyTelemetry.cs"
XAML = ROOT / "dotnet/RepartoCopier.WinUI/MainWindow.xaml"
CODE = ROOT / "dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs"
VERIFY_STREAM_TEST = ROOT / "dotnet/RepartoCopier.Core.Tests/VerificationStreamingArchitectureTests.cs"
VERIFY_ARCH_TEST = ROOT / "dotnet/RepartoCopier.Core.Tests/VerificationArchitectureTests.cs"
UI_TEST = ROOT / "dotnet/RepartoCopier.Core.Tests/WinUiCompactProgressContractTests.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"missing expected marker: {label}")
    return text.replace(old, new, 1)


def replace_regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"expected one regex match for {label}, got {count}")
    return updated

# ---------------------------------------------------------------------------
# Core: free the 256 MiB FAN-OUT pool before verification, then use one
# contiguous fixed verification workspace split among source + destinations.
# ---------------------------------------------------------------------------
engine = ENGINE.read_text(encoding="utf-8")
engine = replace_once(
    engine,
    "        using var resources = new ResourceGovernor();\n        using var bufferPool = new SharedFanoutBufferPool(checked((int)SharedFanoutPoolBytes));\n        var controlBudget = AdaptiveControlByteBudget.CreateForSystem();",
    "        using var resources = new ResourceGovernor();\n        SharedFanoutBufferPool? bufferPool = null;\n        var controlBudget = AdaptiveControlByteBudget.CreateForSystem();",
    "copy pool declaration",
)
engine = replace_once(
    engine,
    "            var copyPhaseStarted = Stopwatch.GetTimestamp();\n            var writerTasks = workers",
    "            var copyPhaseStarted = Stopwatch.GetTimestamp();\n            var activeBufferPool = bufferPool = new SharedFanoutBufferPool(checked((int)SharedFanoutPoolBytes));\n            var writerTasks = workers",
    "copy pool activation",
)
engine = replace_once(
    engine,
    "                    job,\n                    bufferPool,\n                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);",
    "                    job,\n                    activeBufferPool,\n                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);",
    "productive pool parameter",
)
engine = replace_once(
    engine,
    "            if (writerError is not null)\n                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();\n\n            if (options.Verify && !token.IsCancellationRequested)",
    "            if (writerError is not null)\n                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();\n\n            // COPY owns the large 256 MiB page pool. Verification deliberately does not.\n            // Once every writer drained, release that pinned/locked region before VERIFY.\n            activeBufferPool.Dispose();\n            bufferPool = null;\n\n            if (options.Verify && !token.IsCancellationRequested)",
    "pool release before verify",
)
engine = replace_once(
    engine,
    "            foreach (var worker in workers)\n            {\n                worker.Channel.Writer.TryComplete();\n                DrainAndRelease(worker.Channel.Reader, worker);\n            }\n            copy.ReleaseStateLeases();",
    "            foreach (var worker in workers)\n            {\n                worker.Channel.Writer.TryComplete();\n                DrainAndRelease(worker.Channel.Reader, worker);\n            }\n            bufferPool?.Dispose();\n            copy.ReleaseStateLeases();",
    "pool cleanup fallback",
)

verify_block = r'''    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        CopyJob job,
        DeviceScheduler? sharedSourceScheduler)
    {
        for (var slot = 0; slot < workers.Length; slot++)
        {
            if (!workers[slot].IsActive)
                continue;
            var entries = copy.Files
                .Where(entry => workers[slot].CompletedFiles.Contains(PathKey(entry.RelativePath)))
                .ToArray();
            var bytes = entries.Aggregate<FileEntry, ulong>(0, (sum, entry) => checked(sum + (ulong)entry.Size));
            progress[slot].SetVerifyWork(bytes, (ulong)entries.Length);
            if (entries.Length > 0)
                progress[slot].SetPhase(DestinationPhase.Verifying);
        }

        foreach (var entry in copy.Files)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            var slots = Enumerable.Range(0, workers.Length)
                .Where(slot => workers[slot].IsActive && workers[slot].CompletedFiles.Contains(PathKey(entry.RelativePath)))
                .ToArray();
            if (slots.Length == 0)
                continue;

            ValidateSourceSnapshot(entry);
            var targets = new List<CoordinatedVerifyTarget>(slots.Length + 1);
            try
            {
                targets.Add(new CoordinatedVerifyTarget(
                    slot: -1,
                    path: entry.SourcePath,
                    device: copy.SourceDevice,
                    scheduler: sharedSourceScheduler,
                    progress: null));

                foreach (var slot in slots)
                {
                    progress[slot].SetLastFile(entry.RelativePath);
                    var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                    if (!File.Exists(destination))
                    {
                        workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                        continue;
                    }
                    ValidateRuntimeDestinationPath(workers[slot].Root, entry.RelativePath);
                    WindowsPath.EnsureRegularFile(destination, "El archivo durante verificación");
                    if (new FileInfo(destination).Length != entry.Size)
                    {
                        workers[slot].Fail($"Tamaño no coincide durante verificación: {destination}");
                        continue;
                    }
                    targets.Add(new CoordinatedVerifyTarget(
                        slot,
                        destination,
                        copy.DestinationDevices[slot],
                        workers[slot].DeviceScheduler,
                        progress[slot]));
                }

                if (targets.Count <= 1)
                    continue;

                using var workspace = new VerificationWorkspace(targets);
                var perStreamBytes = workspace.PerStreamBytes;
                for (var index = 0; index < targets.Count; index++)
                    targets[index].Open(workspace.RentSlice(index));

                long offset = 0;
                while (offset < entry.Size)
                {
                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

                    var activeTargets = targets
                        .Where(target => target.IsSource || workers[target.Slot].IsActive)
                        .ToArray();
                    if (activeTargets.Length <= 1)
                        break;

                    var expectedBytes = (int)Math.Min(perStreamBytes, entry.Size - offset);
                    var reads = activeTargets
                        .Select(target => ReadVerifyTargetAsync(target, expectedBytes, offset, job))
                        .ToArray();
                    var results = await Task.WhenAll(reads).ConfigureAwait(false);

                    var sourceIndex = Array.FindIndex(activeTargets, target => target.IsSource);
                    if (sourceIndex < 0 || results[sourceIndex] < expectedBytes)
                        throw new IOException($"Lectura incompleta del origen durante verificación: {entry.SourcePath}");

                    var crcStarted = Stopwatch.GetTimestamp();
                    var sourceCrc = FastCrc32C.Compute(activeTargets[sourceIndex].Buffer!.Memory.Span[..expectedBytes]);
                    job.Telemetry.RecordVerifyCrc32C(expectedBytes, Stopwatch.GetElapsedTime(crcStarted));

                    for (var index = 0; index < activeTargets.Length; index++)
                    {
                        var target = activeTargets[index];
                        if (target.IsSource)
                            continue;
                        if (results[index] < expectedBytes)
                        {
                            workers[target.Slot].Fail($"Lectura incompleta durante verificación: {target.Path}");
                            continue;
                        }

                        crcStarted = Stopwatch.GetTimestamp();
                        var destinationCrc = FastCrc32C.Compute(target.Buffer!.Memory.Span[..expectedBytes]);
                        job.Telemetry.RecordVerifyCrc32C(expectedBytes, Stopwatch.GetElapsedTime(crcStarted));
                        if (destinationCrc != sourceCrc)
                        {
                            workers[target.Slot].Fail($"CRC32C no coincide durante verificación: {target.Path}");
                            continue;
                        }
                        target.Progress!.AddVerified(expectedBytes);
                    }

                    // Source-equivalent progress: one logical chunk, regardless of destination count.
                    job.Telemetry.RecordVerifyLogicalBytes(expectedBytes);
                    offset = checked(offset + expectedBytes);
                }

                ValidateSourceSnapshot(entry);
                if (offset != entry.Size)
                {
                    foreach (var target in targets.Where(target => !target.IsSource && workers[target.Slot].IsActive))
                        workers[target.Slot].Fail($"Verificación incompleta: {target.Path}");
                }
                else
                {
                    foreach (var target in targets.Where(target => !target.IsSource && workers[target.Slot].IsActive))
                        target.Progress!.MarkVerifyFileDone();
                }
            }
            finally
            {
                foreach (var target in targets)
                    target.Dispose();
            }
        }
    }

    private static async Task<int> ReadVerifyTargetAsync(
        CoordinatedVerifyTarget target,
        int expectedBytes,
        long offset,
        CopyJob job)
    {
        var requestBytes = target.Direct is null
            ? expectedBytes
            : AlignUp(expectedBytes, target.Direct.Alignment);

        IDisposable? io = null;
        if (target.Scheduler is not null)
            io = await target.Scheduler.AcquireIoAsync(requestBytes, job.Token).ConfigureAwait(false);
        using (io)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                int read;
                if (target.Direct is not null)
                {
                    try
                    {
                        read = await target.Direct.ReadAsync(
                            target.Buffer!, requestBytes, offset, job.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                    {
                        target.SwitchToBuffered();
                        return await ReadVerifyTargetAsync(target, expectedBytes, offset, job).ConfigureAwait(false);
                    }
                }
                else
                {
                    read = await RandomAccess.ReadAsync(
                        target.BufferedHandle!,
                        target.Buffer!.Memory[..expectedBytes],
                        offset,
                        job.Token).ConfigureAwait(false);
                }
                job.Telemetry.RecordVerifyRead(expectedBytes, Stopwatch.GetElapsedTime(started));
                return read;
            }
            catch (Exception ex) when (
                target.Scheduler is not null &&
                TransientIoErrorClassifier.IsTransient(ex) &&
                target.Scheduler.RecordTransientFailure())
            {
                target.Progress?.AddRetry();
                job.Telemetry.RecordIoRecovery(
                    "verify-read",
                    target.Path,
                    target.Direct is null ? "buffered" : "direct",
                    TransientIoErrorClassifier.GetNativeCodeOrZero(ex),
                    target.Scheduler.CurrentQueueDepth,
                    retryCount: 1,
                    offset,
                    recovered: false);
                return await ReadVerifyTargetAsync(target, expectedBytes, offset, job).ConfigureAwait(false);
            }
        }
    }

    private static int AlignUp(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private sealed class VerificationWorkspace : IDisposable
    {
        private readonly byte[] _buffer;
        private readonly int _baseOffset;
        private readonly int _streamCount;
        private int _disposed;

        internal VerificationWorkspace(IReadOnlyList<CoordinatedVerifyTarget> targets)
        {
            ArgumentNullException.ThrowIfNull(targets);
            if (targets.Count <= 1)
                throw new ArgumentOutOfRangeException(nameof(targets));

            var alignment = targets
                .Select(target => Math.Max(Environment.SystemPageSize, DirectIoSourceReader.RequiredAlignment(target.Device)))
                .Max();
            var rawPerStream = VerificationWorkspaceBytes / targets.Count;
            PerStreamBytes = rawPerStream - rawPerStream % alignment;
            if (PerStreamBytes < alignment)
                throw new IOException("Demasiados destinos para el workspace fijo de verificación.");

            _streamCount = targets.Count;
            _buffer = GC.AllocateUninitializedArray<byte>(checked(VerificationWorkspaceBytes + alignment), pinned: true);
            var rawPointer = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0).ToInt64();
            var remainder = rawPointer % alignment;
            var alignedPointer = remainder == 0 ? rawPointer : checked(rawPointer + alignment - remainder);
            _baseOffset = checked((int)(alignedPointer - rawPointer));
        }

        internal int PerStreamBytes { get; }

        internal SourceBufferLease RentSlice(int index)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if ((uint)index >= (uint)_streamCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            var offset = checked(_baseOffset + index * PerStreamBytes);
            var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, offset);
            return SourceBufferLease.BorrowPinned(_buffer, offset, PerStreamBytes, pointer);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class CoordinatedVerifyTarget : IDisposable
    {
        internal CoordinatedVerifyTarget(
            int slot,
            string path,
            StorageDeviceInfo device,
            DeviceScheduler? scheduler,
            DestinationProgress? progress)
        {
            Slot = slot;
            Path = path;
            Device = device;
            Scheduler = scheduler;
            Progress = progress;
        }

        internal bool IsSource => Slot < 0;
        internal int Slot { get; }
        internal string Path { get; }
        internal StorageDeviceInfo Device { get; }
        internal DeviceScheduler? Scheduler { get; }
        internal DestinationProgress? Progress { get; }
        internal DirectIoSourceReader.OverlappedSession? Direct { get; private set; }
        internal Microsoft.Win32.SafeHandles.SafeFileHandle? BufferedHandle { get; private set; }
        internal SourceBufferLease? Buffer { get; private set; }

        internal void Open(SourceBufferLease buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Buffer = buffer;
            if (DirectIoSourceReader.TryOpenOverlappedForVerification(Path, Device, buffer.Capacity, out var direct))
            {
                Direct = direct;
                return;
            }
            BufferedHandle = File.OpenHandle(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        internal void SwitchToBuffered()
        {
            Direct?.Dispose();
            Direct = null;
            BufferedHandle ??= File.OpenHandle(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public void Dispose()
        {
            Direct?.Dispose();
            BufferedHandle?.Dispose();
            Buffer?.Dispose();
        }
    }

'''

pattern = r"    private static async Task VerifyDestinationsAsync\(.*?(?=    private static async Task<bool\[\]\[]> BuildVerifiedSkipMasksAsync)"
engine = replace_regex_once(engine, pattern, verify_block, "verification pipeline")
ENGINE.write_text(engine, encoding="utf-8", newline="\n")

# ---------------------------------------------------------------------------
# Telemetry: add a source-equivalent rolling verify rate for UI speed and ETA.
# ---------------------------------------------------------------------------
telemetry = TELEMETRY.read_text(encoding="utf-8")
telemetry = replace_once(
    telemetry,
    "    public double SourceRead10sBytesPerSecond { get; init; }\n    public IReadOnlyList<DeviceIoSnapshot> DeviceSchedulers",
    "    public double SourceRead10sBytesPerSecond { get; init; }\n    public double VerifyLogical5sBytesPerSecond { get; init; }\n    public double VerifyLogical10sBytesPerSecond { get; init; }\n    public IReadOnlyList<DeviceIoSnapshot> DeviceSchedulers",
    "verify rate snapshot properties",
)
telemetry = replace_once(
    telemetry,
    "    private readonly SlidingByteRateWindow _sourceReadRate = new();\n    private readonly ConcurrentQueue<IoRecoveryEvent>",
    "    private readonly SlidingByteRateWindow _sourceReadRate = new();\n    private readonly SlidingByteRateWindow _verifyLogicalRate = new();\n    private readonly ConcurrentQueue<IoRecoveryEvent>",
    "verify rolling window",
)
telemetry = replace_once(
    telemetry,
    "    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }\n    internal void RecordVerifyCrc32C",
    "    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }\n    internal void RecordVerifyLogicalBytes(int bytes) { if (bytes > 0) _verifyLogicalRate.Record(bytes); }\n    internal void RecordVerifyCrc32C",
    "verify logical recorder",
)
telemetry = replace_once(
    telemetry,
    "        var sustained = _writeRate.Snapshot();\n        var sourceSustained = _sourceReadRate.Snapshot();",
    "        var sustained = _writeRate.Snapshot();\n        var sourceSustained = _sourceReadRate.Snapshot();\n        var verifySustained = _verifyLogicalRate.Snapshot();",
    "verify snapshot capture",
)
telemetry = replace_once(
    telemetry,
    "            SourceRead10sBytesPerSecond = sourceSustained.TenSecondsBytesPerSecond,\n            DeviceSchedulers = devices,",
    "            SourceRead10sBytesPerSecond = sourceSustained.TenSecondsBytesPerSecond,\n            VerifyLogical5sBytesPerSecond = verifySustained.FiveSecondsBytesPerSecond,\n            VerifyLogical10sBytesPerSecond = verifySustained.TenSecondsBytesPerSecond,\n            DeviceSchedulers = devices,",
    "verify snapshot assignment",
)
TELEMETRY.write_text(telemetry, encoding="utf-8", newline="\n")

# ---------------------------------------------------------------------------
# UI: compact shell, horizontal destination chips, proper Mica layer, inactive
# title-bar affordance, and real verification throughput/ETA.
# ---------------------------------------------------------------------------
xaml = XAML.read_text(encoding="utf-8")
xaml = xaml.replace('Background" Value="{ThemeResource CardBackgroundFillColorDefaultBrush}"', 'Background" Value="{ThemeResource LayerFillColorDefaultBrush}"')
xaml = xaml.replace('<Setter Property="Height" Value="34" />', '<Setter Property="Height" Value="32" />', 1)
xaml = xaml.replace('<Setter Property="MinWidth" Value="76" />', '<Setter Property="MinWidth" Value="70" />', 1)
xaml = xaml.replace('<RowDefinition Height="48" />', '<RowDefinition Height="36" />', 1)
xaml = xaml.replace('<RowDefinition Height="28" />', '<RowDefinition Height="26" />', 1)
xaml = xaml.replace('x:Name="AppTitleBar" Grid.Row="0" Padding="14,0,142,0"', 'x:Name="AppTitleBar" Grid.Row="0" Padding="12,0,138,0"')
xaml = xaml.replace('Width="28" Height="28" Source="ms-appx:///Assets/AppLogo.png"', 'Width="22" Height="22" Source="ms-appx:///Assets/AppLogo.png"')
xaml = replace_regex_once(
    xaml,
    r'<StackPanel Grid.Column="1" Margin="9,0,0,0" VerticalAlignment="Center" Spacing="0">\s*<TextBlock Text="RepartoCopier" FontSize="15" FontWeight="SemiBold" />\s*<TextBlock Text="FAN-OUT" Opacity="0\.56" FontSize="10" />\s*</StackPanel>',
    '<TextBlock x:Name="TitleBarText" Grid.Column="1" Margin="8,0,0,0" VerticalAlignment="Center" Text="RepartoCopier" FontSize="13" FontWeight="SemiBold" />',
    "compact title bar",
)
xaml = xaml.replace('Grid.Column="3" Width="34" Height="32"', 'Grid.Column="3" Width="30" Height="28"')
xaml = xaml.replace('Grid Grid.Row="1" Margin="14,4,14,10" MaxWidth="820"', 'Grid Grid.Row="1" Margin="12,2,12,8" MaxWidth="780"')
xaml = xaml.replace('MaxWidth="720" HorizontalAlignment="Center"', 'MaxWidth="680" HorizontalAlignment="Center"', 1)
xaml = xaml.replace('Text="Nueva copia" FontSize="20"', 'Text="Nueva copia" FontSize="18"')
xaml = xaml.replace('Style="{StaticResource SurfaceCardStyle}" Padding="12"', 'Style="{StaticResource SurfaceCardStyle}" Padding="10"', 1)
xaml = xaml.replace('<Grid RowSpacing="10">', '<Grid RowSpacing="8">', 1)
xaml = xaml.replace('Height="36" PlaceholderText="Archivo o carpeta de origen"', 'Height="34" PlaceholderText="Archivo o carpeta de origen"')

new_list = '''                        <ListView x:Name="DestinationList" Grid.Row="3" SelectionMode="None" Height="50" MaxHeight="50"
                                  Padding="0" IsTabStop="False"
                                  ScrollViewer.HorizontalScrollMode="Enabled"
                                  ScrollViewer.HorizontalScrollBarVisibility="Auto"
                                  ScrollViewer.VerticalScrollMode="Disabled"
                                  ScrollViewer.VerticalScrollBarVisibility="Disabled">
                            <ListView.ItemsPanel>
                                <ItemsPanelTemplate>
                                    <ItemsStackPanel Orientation="Horizontal" />
                                </ItemsPanelTemplate>
                            </ListView.ItemsPanel>
                            <ListView.ItemContainerStyle>
                                <Style TargetType="ListViewItem">
                                    <Setter Property="Padding" Value="0"/>
                                    <Setter Property="Margin" Value="0,0,6,0"/>
                                    <Setter Property="MinHeight" Value="42"/>
                                    <Setter Property="CornerRadius" Value="6"/>
                                </Style>
                            </ListView.ItemContainerStyle>
                            <ListView.ItemTemplate>
                                <DataTemplate>
                                    <Border Width="158" Height="42" Padding="8,0" CornerRadius="6"
                                            Background="{ThemeResource ControlFillColorDefaultBrush}"
                                            BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}" BorderThickness="1"
                                            ToolTipService.ToolTip="{Binding Path}">
                                        <Grid ColumnSpacing="7">
                                            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                                            <FontIcon Glyph="&#xEDA2;" FontSize="13" Opacity="0.68" VerticalAlignment="Center"/>
                                            <TextBlock Grid.Column="1" Text="{Binding Path}" FontSize="11" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                                            <Button Grid.Column="2" Width="24" Height="24" Padding="0" CornerRadius="5" Tag="{Binding Path}" Click="RemoveDestination_Click" ToolTipService.ToolTip="Quitar">
                                                <FontIcon Glyph="&#xE711;" FontSize="10"/>
                                            </Button>
                                        </Grid>
                                    </Border>
                                </DataTemplate>
                            </ListView.ItemTemplate>
                        </ListView>'''
xaml = replace_regex_once(
    xaml,
    r'                        <ListView x:Name="DestinationList".*?                        </ListView>',
    new_list,
    "horizontal destination list",
)
xaml = xaml.replace('MaxWidth="760" HorizontalAlignment="Center"', 'MaxWidth="700" HorizontalAlignment="Center"')
xaml = xaml.replace('Style="{StaticResource SurfaceCardStyle}" Padding="14,12"', 'Style="{StaticResource SurfaceCardStyle}" Padding="12,10"')
XAML.write_text(xaml, encoding="utf-8", newline="\n")

code = CODE.read_text(encoding="utf-8")
code = code.replace('AppWindow.Resize(new SizeInt32(840, 520));', 'AppWindow.Resize(new SizeInt32(800, 460));')
code = replace_once(
    code,
    '        Closed += MainWindow_Closed;\n        TryLoadLaunchSource();',
    '        Closed += MainWindow_Closed;\n        Activated += MainWindow_Activated;\n        TryLoadLaunchSource();',
    "window activation subscription",
)
code = replace_once(
    code,
    '    private void ApplySavedTheme()\n    {',
    '    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)\n    {\n        var active = args.WindowActivationState != WindowActivationState.Deactivated;\n        TitleBarText.Opacity = active ? 1.0 : 0.58;\n        LogoImage.Opacity = active ? 1.0 : 0.58;\n        AppMenuButton.Opacity = active ? 1.0 : 0.72;\n    }\n\n    private void ApplySavedTheme()\n    {',
    "inactive title bar state",
)
old_verify_ui = '''            OverallDetailText.Text = $"Verificados {FormatBytes(verified)} de {FormatBytes(verifyTotal)}";
            SpeedMetricText.Text = paused ? "0.0 B/s" : "—";
            RemainingMetricText.Text = "--:--:--";'''
new_verify_ui = '''            OverallDetailText.Text = $"Verificados {FormatBytes(verified)} de {FormatBytes(verifyTotal)}";
            var diagnostics = _job.DiagnosticsSnapshot();
            var speed = paused ? 0d : diagnostics.VerifyLogical5sBytesPerSecond;
            SpeedMetricText.Text = paused ? "0.0 B/s" : Throughput.Format(speed);
            var remaining = verifyTotal > verified ? verifyTotal - verified : 0;
            RemainingMetricText.Text = !paused && speed > 1
                ? FormatDuration(TimeSpan.FromSeconds(remaining / speed))
                : "--:--:--";'''
code = replace_once(code, old_verify_ui, new_verify_ui, "verify speed and ETA UI")
CODE.write_text(code, encoding="utf-8", newline="\n")

# ---------------------------------------------------------------------------
# Contracts: protect the exact cut we just made.
# ---------------------------------------------------------------------------
VERIFY_STREAM_TEST.write_text('''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationStreamingArchitectureTests
{
    [TestMethod]
    public void VerifyUsesOneFixedContiguousWorkspaceAndNoPerTargetRentals()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("VerificationWorkspaceBytes = 8 * 1024 * 1024", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("sealed class VerificationWorkspace", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("GC.AllocateUninitializedArray<byte>", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("SourceBufferLease.BorrowPinned", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("targets[index].Open(workspace.RentSlice(index))", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlan", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationBlock", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationPlans", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VerifyReadsSourceAndDestinationsTogetherAndComparesImmediately()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var sourceCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("var destinationCrc = FastCrc32C.Compute", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("destinationCrc != sourceCrc", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("RecordVerifyLogicalBytes(expectedBytes)", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
''', encoding="utf-8", newline="\n")

VERIFY_ARCH_TEST.write_text('''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationArchitectureTests
{
    [TestMethod]
    public void VerificationUsesDirectSequentialReadsWithFixedMemory()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("TryOpenOverlappedForVerification", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("FileOptions.Asynchronous | FileOptions.SequentialScan", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("VerificationWorkspace", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "FastVerificationReader.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "VerificationReadBudget.cs")));
    }

    [TestMethod]
    public void LargeCopyPoolIsReleasedBeforeVerifyStarts()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var release = engine.IndexOf("activeBufferPool.Dispose();", StringComparison.Ordinal);
        var verify = engine.IndexOf("if (options.Verify && !token.IsCancellationRequested)", StringComparison.Ordinal);
        Assert.IsTrue(release >= 0 && verify > release);
        Assert.IsTrue(engine.Contains("bufferPool?.Dispose();", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("VerificationCrc32C", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
''', encoding="utf-8", newline="\n")

UI_TEST.write_text('''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class WinUiCompactProgressContractTests
{
    [TestMethod]
    public void RunningViewUsesOnlyOverallJobProgress()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("x:Name=\"OverallProgressBar\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProgressList", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_progressRows", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PreparationViewIsCompactAndDestinationsAreHorizontal()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("SizeInt32(800, 460)", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("MaxWidth=\"780\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Height=\"50\" MaxHeight=\"50\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("<ItemsStackPanel Orientation=\"Horizontal\" />", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("ScrollViewer.HorizontalScrollMode=\"Enabled\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("Header=\"Opciones\" IsExpanded=\"False\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowUsesWindows11MicaLayeringAndInactiveTitleState()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("LayerFillColorDefaultBrush", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("ExtendsContentIntoTitleBar = true", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SetTitleBar(AppTitleBar)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SystemBackdrop = new MicaBackdrop()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("Activated += MainWindow_Activated", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("WindowActivationState.Deactivated", StringComparison.Ordinal));
    }

    [TestMethod]
    public void VerifyShowsRealSourceEquivalentSpeedAndEta()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        var telemetry = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyTelemetry.cs"));
        Assert.IsTrue(code.Contains("diagnostics.VerifyLogical5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(telemetry.Contains("VerifyLogical5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(telemetry.Contains("_verifyLogicalRate", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SpeedMetricText.Text = paused ? \"0.0 B/s\" : \"—\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OverallProgressUsesLogicalJobBytesNotAggregateDestinationBytes()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("snapshots.Select(item => item.Total).DefaultIfEmpty(0UL).Max()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("active.Min(item => item.Written)", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("snapshots.Select(item => item.VerifyBytesTotal).DefaultIfEmpty(0UL).Max()", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("verifyActive.Min(item => item.VerifiedBytes)", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
''', encoding="utf-8", newline="\n")

print("final Extreme alignment + compact UI migration applied")
