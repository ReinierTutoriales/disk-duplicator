from pathlib import Path

root = Path(__file__).resolve().parents[1]

# UI: official WinUI compact density + native TitleBar + denser running view.
app = root / 'dotnet/RepartoCopier.WinUI/App.xaml'
text = app.read_text(encoding='utf-8')
old = '''    <Application.Resources>\n        <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />\n    </Application.Resources>'''
new = '''    <Application.Resources>\n        <ResourceDictionary>\n            <ResourceDictionary.MergedDictionaries>\n                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />\n                <ResourceDictionary Source="ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml" />\n            </ResourceDictionary.MergedDictionaries>\n        </ResourceDictionary>\n    </Application.Resources>'''
if old not in text:
    raise SystemExit('App.xaml resource block not found')
app.write_text(text.replace(old, new), encoding='utf-8')

xaml = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml'
text = xaml.read_text(encoding='utf-8')
text = text.replace('<RowDefinition Height="36" />', '<RowDefinition Height="Auto" />', 1)
start = text.index('        <Grid x:Name="AppTitleBar"')
end_marker = '        <Grid Grid.Row="1" Margin="12,2,12,8" MaxWidth="780" HorizontalAlignment="Stretch">'
end = text.index(end_marker)
new_title = '''        <TitleBar x:Name="AppTitleBar" Grid.Row="0" Title="RepartoCopier">\n            <TitleBar.IconSource>\n                <ImageIconSource ImageSource="ms-appx:///Assets/AppLogo.png" />\n            </TitleBar.IconSource>\n            <TitleBar.RightHeader>\n                <Button x:Name="AppMenuButton" Width="30" Height="28" Padding="0" CornerRadius="6" ToolTipService.ToolTip="Menú">\n                    <FontIcon Glyph="&#xE700;" FontSize="16" />\n                    <Button.Flyout>\n                        <Flyout Placement="BottomEdgeAlignedRight">\n                            <Border Width="208" Padding="6" CornerRadius="8" Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1">\n                                <StackPanel Spacing="1">\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="LoadProfile_Click"><StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE8E5;" FontSize="16"/><TextBlock Text="Cargar copia..." /></StackPanel></Button>\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="SaveProfile_Click"><StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE74E;" FontSize="16"/><TextBlock Text="Guardar copia..." /></StackPanel></Button>\n                                    <Rectangle Height="1" Margin="6,4" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="Settings_Click"><StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE713;" FontSize="16"/><TextBlock Text="Ajustes" /></StackPanel></Button>\n                                    <Button x:Name="AboutMenuButton" Style="{StaticResource MenuButtonStyle}" Click="About_Click"><StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE946;" FontSize="16"/><TextBlock Text="Acerca de" /></StackPanel></Button>\n                                    <Rectangle Height="1" Margin="6,4" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />\n                                    <Button Style="{StaticResource MenuButtonStyle}" Click="Exit_Click"><StackPanel Orientation="Horizontal" Spacing="10"><FontIcon Glyph="&#xE8BB;" FontSize="16"/><TextBlock Text="Salir" /></StackPanel></Button>\n                                </StackPanel>\n                            </Border>\n                        </Flyout>\n                    </Button.Flyout>\n                </Button>\n            </TitleBar.RightHeader>\n        </TitleBar>\n\n'''
text = text[:start] + new_title + text[end:]
text = text.replace(end_marker, '        <Grid Grid.Row="1" Margin="8,0,8,6" MaxWidth="760" HorizontalAlignment="Stretch">')
text = text.replace('MaxWidth="680" HorizontalAlignment="Center"', 'MaxWidth="744" HorizontalAlignment="Stretch"', 1)
text = text.replace('MaxWidth="700" HorizontalAlignment="Center" VerticalAlignment="Center"', 'MaxWidth="744" HorizontalAlignment="Stretch" VerticalAlignment="Top" Margin="0,10,0,0"', 1)
text = text.replace('<Border Style="{StaticResource SurfaceCardStyle}" Padding="12,10">', '<Border Style="{StaticResource SurfaceCardStyle}" Padding="10,8">', 1)
text = text.replace('<Grid RowSpacing="11">', '<Grid RowSpacing="8">', 1)
text = text.replace('<ProgressBar x:Name="OverallProgressBar" Height="6" Minimum="0" Maximum="100" Value="0" />', '''<ProgressBar x:Name="OverallProgressBar" Height="10" MinHeight="10" Minimum="0" Maximum="100" Value="0">\n                                <ProgressBar.Resources>\n                                    <x:Double x:Key="ProgressBarTrackHeight">10</x:Double>\n                                </ProgressBar.Resources>\n                            </ProgressBar>''', 1)
text = text.replace('Margin="0,5,0,0" Text="Preparando..."', 'Margin="0,3,0,0" Text="Preparando..."', 1)
xaml.write_text(text, encoding='utf-8')

code = root / 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
text = code.read_text(encoding='utf-8')
text = text.replace('    private DateTimeOffset? _copyStartedAt;\n', '    private DateTimeOffset? _copyStartedAt;\n    private readonly LogicalProgressRate _copyProgressRate = new();\n')
text = text.replace('        AppWindow.Resize(new SizeInt32(800, 460));', '        AppWindow.Resize(new SizeInt32(760, 400));')
text = text.replace('        Activated += MainWindow_Activated;\n', '')
method_start = text.find('    private void MainWindow_Activated(')
if method_start >= 0:
    method_end = text.index('    private void ApplySavedTheme()', method_start)
    text = text[:method_start] + text[method_end:]
text = text.replace('            _copyStartedAt = DateTimeOffset.Now;\n', '            _copyStartedAt = DateTimeOffset.Now;\n            _copyProgressRate.Reset();\n')
old_speed = '''            var diagnostics = _job.DiagnosticsSnapshot();\n            var speed = paused ? 0d : diagnostics.SourceRead5sBytesPerSecond;\n            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);'''
new_speed = '''            var speed = paused ? 0d : _copyProgressRate.Observe(written);\n            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);'''
if old_speed not in text:
    raise SystemExit('copy speed UI block not found')
text = text.replace(old_speed, new_speed, 1)
insert = '''\n    private sealed class LogicalProgressRate\n    {\n        private long _lastTimestamp;\n        private ulong _lastBytes;\n        private double _smoothedBytesPerSecond;\n\n        internal void Reset()\n        {\n            _lastTimestamp = 0;\n            _lastBytes = 0;\n            _smoothedBytesPerSecond = 0;\n        }\n\n        internal double Observe(ulong bytes)\n        {\n            var now = Stopwatch.GetTimestamp();\n            if (_lastTimestamp == 0 || bytes < _lastBytes)\n            {\n                _lastTimestamp = now;\n                _lastBytes = bytes;\n                _smoothedBytesPerSecond = 0;\n                return 0;\n            }\n            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;\n            if (elapsed < 0.15) return _smoothedBytesPerSecond;\n            var delta = bytes - _lastBytes;\n            var instant = delta / elapsed;\n            _lastTimestamp = now;\n            _lastBytes = bytes;\n            if (delta == 0)\n            {\n                _smoothedBytesPerSecond *= 0.82;\n                if (_smoothedBytesPerSecond < 1024) _smoothedBytesPerSecond = 0;\n                return _smoothedBytesPerSecond;\n            }\n            _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0 ? instant : (_smoothedBytesPerSecond * 0.70) + (instant * 0.30);\n            return _smoothedBytesPerSecond;\n        }\n    }\n'''
marker = '\n    private static string FormatDestinationCount(int count) =>'
if marker not in text:
    raise SystemExit('MainWindow helper marker not found')
text = text.replace(marker, insert + marker, 1)
code.write_text(text, encoding='utf-8')

# Extreme-style local I/O policy: one write in flight per physical device.
# Keep the 256 MiB shared FAN-OUT pool; remove adaptive QD exploration entirely.
profile = root / 'dotnet/RepartoCopier.Core/StorageIoProfile.cs'
profile.write_text(r'''namespace RepartoCopier.Core;

public enum StorageProfileKind
{
    Conservative,
    Network,
    Rotational,
    UsbFlash,
    UsbSsd,
    SataSsd,
    Nvme,
    Virtual,
    StorageSpaces,
}

/// <summary>
/// Storage classification and soft backlog accounting only.
/// RepartoCopier intentionally keeps one physical write in flight per device,
/// matching the stable synchronous destination flow used by ExtremeCopy.
/// </summary>
public sealed record StorageIoProfile(
    StorageProfileKind Kind,
    int InitialQueueDepth,
    long DeviceBacklogTargetBytes)
{
    private const int MiB = 1024 * 1024;
    private const long ConservativeBacklog = 64L * MiB;
    private const long RotationalBacklog = 128L * MiB;
    private const long UsbFlashBacklog = 128L * MiB;
    private const long UsbSsdBacklog = 256L * MiB;
    private const long SataSsdBacklog = 256L * MiB;
    private const long NvmeBacklog = 512L * MiB;
    private const long NetworkBacklog = 32L * MiB;

    public static StorageIoProfile For(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.IsNetwork) return new(StorageProfileKind.Network, 1, NetworkBacklog);
        if (device.MediaKind == StorageMediaKind.Rotational) return new(StorageProfileKind.Rotational, 1, RotationalBacklog);
        if (string.Equals(device.BusType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            var ssd = device.MediaKind == StorageMediaKind.SolidState && device.TrimEnabled == true;
            return new(ssd ? StorageProfileKind.UsbSsd : StorageProfileKind.UsbFlash, 1, ssd ? UsbSsdBacklog : UsbFlashBacklog);
        }
        if (device.MediaKind == StorageMediaKind.SolidState && string.Equals(device.BusType, "SATA", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.SataSsd, 1, SataSsdBacklog);
        if (device.MediaKind == StorageMediaKind.SolidState && string.Equals(device.BusType, "NVMe", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.Nvme, 1, NvmeBacklog);
        if (string.Equals(device.BusType, "StorageSpaces", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.StorageSpaces, 1, ConservativeBacklog);
        if (string.Equals(device.BusType, "Virtual", StringComparison.OrdinalIgnoreCase) || string.Equals(device.BusType, "FileBackedVirtual", StringComparison.OrdinalIgnoreCase))
            return new(StorageProfileKind.Virtual, 1, ConservativeBacklog);
        return new(StorageProfileKind.Conservative, 1, ConservativeBacklog);
    }
}
''', encoding='utf-8')

scheduler = root / 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
scheduler.write_text(r'''using System.Threading;

namespace RepartoCopier.Core;

public sealed record DeviceIoSnapshot(
    string DeviceId,
    int InitialQueueDepth,
    int CurrentQueueDepth,
    int ExplorationQueueDepth,
    int OutstandingIo,
    int PeakOutstandingIo,
    int MinimumObservedQueueDepth,
    int MaximumObservedQueueDepth,
    int BestObservedQueueDepth,
    int QueueDepthUpshifts,
    int QueueDepthDownshifts,
    string LastQueueDepthDecision,
    double BestObservedThroughputBytesPerSecond,
    double BestObservedAverageLatencyMilliseconds,
    long BacklogTargetBytes,
    long QueuedBytes,
    long PeakQueuedBytes,
    DeviceIdentityConfidence IdentityConfidence = DeviceIdentityConfidence.Unknown)
{
    public double BacklogPressure => BacklogTargetBytes <= 0 ? 0 : Math.Max(0, (double)QueuedBytes / BacklogTargetBytes);
}

/// <summary>
/// Fixed one-I/O gate per physical device. There is deliberately no queue-depth
/// exploration, throughput probing, multiplicative growth, or latency tuning.
/// This mirrors ExtremeCopy's stable synchronous destination model while the
/// shared 256 MiB FAN-OUT pool provides the buffering/backpressure window.
/// </summary>
internal sealed class DeviceScheduler : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<IoWaiter> _waiters = new();
    private readonly object _backlogGate = new();
    private int _outstandingIo;
    private int _peakOutstandingIo;
    private int _transientFailuresSinceSuccess;
    private long _queuedBytes;
    private long _peakQueuedBytes;
    private bool _disposed;

    internal DeviceScheduler(string deviceId, int initialQueueDepth, long backlogTargetBytes, DeviceIdentityConfidence identityConfidence = DeviceIdentityConfidence.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialQueueDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backlogTargetBytes);
        DeviceId = deviceId;
        BacklogTargetBytes = backlogTargetBytes;
        IdentityConfidence = identityConfidence;
    }

    public string DeviceId { get; }
    public int InitialQueueDepth => 1;
    public int CurrentQueueDepth => 1;
    public int ExplorationQueueDepth => 1;
    public long BacklogTargetBytes { get; }
    public DeviceIdentityConfidence IdentityConfidence { get; }
    public int OutstandingIo => Volatile.Read(ref _outstandingIo);
    public int PeakOutstandingIo => Volatile.Read(ref _peakOutstandingIo);
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    public long PeakQueuedBytes => Interlocked.Read(ref _peakQueuedBytes);

    public DeviceIoSnapshot Snapshot() => new(
        DeviceId, 1, 1, 1, OutstandingIo, PeakOutstandingIo,
        1, 1, 1, 0, 0, "fixed:extreme-style", 0, 0,
        BacklogTargetBytes, QueuedBytes, PeakQueuedBytes, IdentityConfidence);

    public ValueTask<IoLease> AcquireIoAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_outstandingIo == 0 && _waiters.Count == 0)
                return ValueTask.FromResult(GrantLeaseLocked(bytes));
            var waiter = new IoWaiter(bytes);
            _waiters.Enqueue(waiter);
            waiter.Cancellation = token.Register(static state =>
            {
                var pair = ((DeviceScheduler Owner, IoWaiter Waiter, CancellationToken Token))state!;
                pair.Owner.CancelWaiter(pair.Waiter, pair.Token);
            }, (this, waiter, token));
            return new ValueTask<IoLease>(waiter.Completion.Task);
        }
    }

    internal bool RecordTransientFailure()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _transientFailuresSinceSuccess++;
            return _transientFailuresSinceSuccess <= 3;
        }
    }

    public void ReserveBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            var queued = Interlocked.Add(ref _queuedBytes, bytes);
            UpdateMax(ref _peakQueuedBytes, queued);
        }
    }

    public void ReleaseBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            var remaining = Interlocked.Read(ref _queuedBytes) - bytes;
            if (remaining < 0) throw new InvalidOperationException("La cola física intentó liberar más bytes de los reservados.");
            Interlocked.Exchange(ref _queuedBytes, remaining);
        }
    }

    private IoLease GrantLeaseLocked(int bytes)
    {
        _outstandingIo = 1;
        if (_peakOutstandingIo < 1) _peakOutstandingIo = 1;
        return new IoLease(this, bytes);
    }

    private void CancelWaiter(IoWaiter waiter, CancellationToken token)
    {
        lock (_gate)
        {
            if (waiter.Granted || waiter.Cancelled) return;
            waiter.Cancelled = true;
            waiter.Completion.TrySetCanceled(token);
        }
    }

    private void ReleaseIo()
    {
        IoWaiter? ready = null;
        IoLease? lease = null;
        lock (_gate)
        {
            if (_outstandingIo != 1) throw new InvalidOperationException("La contabilidad de I/O físico quedó inválida.");
            _outstandingIo = 0;
            _transientFailuresSinceSuccess = 0;
            while (_waiters.Count > 0)
            {
                var candidate = _waiters.Dequeue();
                if (candidate.Cancelled)
                {
                    candidate.Cancellation.Dispose();
                    continue;
                }
                candidate.Granted = true;
                ready = candidate;
                lease = GrantLeaseLocked(candidate.Bytes);
                break;
            }
        }
        if (ready is not null)
        {
            ready.Cancellation.Dispose();
            ready.Completion.TrySetResult(lease!);
        }
    }

    public void Dispose()
    {
        List<IoWaiter>? cancelled = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_waiters.Count > 0) (cancelled ??= []).Add(_waiters.Dequeue());
        }
        if (cancelled is not null)
        {
            foreach (var waiter in cancelled)
            {
                waiter.Cancelled = true;
                waiter.Cancellation.Dispose();
                waiter.Completion.TrySetException(new ObjectDisposedException(nameof(DeviceScheduler)));
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void UpdateMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed class IoWaiter(int bytes)
    {
        public int Bytes { get; } = bytes;
        public TaskCompletionSource<IoLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; set; }
        public bool Granted { get; set; }
        public bool Cancelled { get; set; }
    }

    internal sealed class IoLease : IDisposable
    {
        private DeviceScheduler? _owner;
        internal IoLease(DeviceScheduler owner, int bytes) { _owner = owner; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseIo();
    }
}

internal sealed class DeviceSchedulerMap : IDisposable
{
    private readonly Dictionary<string, DeviceScheduler> _schedulers;
    private DeviceSchedulerMap(Dictionary<string, DeviceScheduler> schedulers, DeviceScheduler? sharedSourceScheduler)
    {
        _schedulers = schedulers;
        SharedSourceScheduler = sharedSourceScheduler;
    }

    public IReadOnlyCollection<DeviceScheduler> Schedulers => _schedulers.Values;
    public DeviceScheduler? SharedSourceScheduler { get; }
    public IReadOnlyList<DeviceIoSnapshot> Snapshot() => _schedulers.Values.Select(item => item.Snapshot()).OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray();
    public DeviceScheduler For(StorageDeviceInfo device) => _schedulers.TryGetValue(device.PhysicalDeviceId, out var scheduler) ? scheduler : throw new KeyNotFoundException($"No existe scheduler para {device.PhysicalDeviceId}.");

    public static DeviceSchedulerMap Create(StorageDeviceInfo? source, IEnumerable<StorageDeviceInfo> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var groups = destinations.ToArray().GroupBy(item => item.PhysicalDeviceId, StringComparer.OrdinalIgnoreCase);
        var schedulers = new Dictionary<string, DeviceScheduler>(StringComparer.OrdinalIgnoreCase);
        DeviceScheduler? sharedSourceScheduler = null;
        foreach (var group in groups)
        {
            var devices = group.ToArray();
            var profiles = devices.Select(StorageIoProfile.For).ToArray();
            var backlogTarget = profiles.Min(profile => profile.DeviceBacklogTargetBytes);
            var confidence = (DeviceIdentityConfidence)devices.Min(item => (int)StorageDeviceIdentity.ConfidenceFor(item));
            var scheduler = new DeviceScheduler(group.Key, 1, backlogTarget, confidence);
            schedulers.Add(group.Key, scheduler);
            if (source is not null && SharesPhysicalDevice(source, devices[0])) sharedSourceScheduler = scheduler;
        }
        return new DeviceSchedulerMap(schedulers, sharedSourceScheduler);
    }

    internal static bool SharesPhysicalDevice(StorageDeviceInfo left, StorageDeviceInfo right) => StorageDeviceIdentity.SamePhysicalDevice(left, right);

    public void Dispose()
    {
        foreach (var scheduler in _schedulers.Values) scheduler.Dispose();
        _schedulers.Clear();
    }
}
''', encoding='utf-8')

# Replace adaptive-QD tests with permanent Extreme-style stability contracts.
(root / 'dotnet/RepartoCopier.Core.Tests/AdaptiveQueueDepthArchitectureTests.cs').unlink(missing_ok=True)
(root / 'dotnet/RepartoCopier.Core.Tests/ExtremeStyleIoArchitectureTests.cs').write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ExtremeStyleIoArchitectureTests
{
    [TestMethod]
    public void SchedulerContainsNoAdaptiveQueueDepthExploration()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DeviceScheduler.cs"));
        Assert.IsFalse(code.Contains("UpshiftLocked", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DownshiftToBestLocked", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_bestThroughputBytesPerSecond", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_samplePeakObservedConcurrency", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("public int CurrentQueueDepth => 1;", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("public int ExplorationQueueDepth => 1;", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("fixed:extreme-style", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EveryStorageClassUsesOnePhysicalIoAtATime()
    {
        var devices = new[]
        {
            Device("USB", StorageMediaKind.SolidState, true),
            Device("SATA", StorageMediaKind.SolidState, true),
            Device("NVMe", StorageMediaKind.SolidState, true),
            Device("SATA", StorageMediaKind.Rotational, false),
        };
        foreach (var device in devices) Assert.AreEqual(1, StorageIoProfile.For(device).InitialQueueDepth);
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media, bool? trim) =>
        new(@"E:\copy", @"E:\", 4, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);

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
''', encoding='utf-8')

(root / 'dotnet/RepartoCopier.Core.Tests/DeviceSchedulerTests.cs').write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DeviceSchedulerTests
{
    [TestMethod]
    public void DestinationsOnSamePhysicalDiskShareOneFixedScheduler()
    {
        var source = Device(@"C:\source", 1, "NVMe", StorageMediaKind.SolidState, true);
        var first = Device(@"E:\copy", 4, "SATA", StorageMediaKind.SolidState, true);
        var second = Device(@"F:\copy", 4, "USB", StorageMediaKind.SolidState, true);
        using var map = DeviceSchedulerMap.Create(source, [first, second]);
        Assert.HasCount(1, map.Schedulers);
        Assert.AreSame(map.For(first), map.For(second));
        Assert.AreEqual(1, map.For(first).CurrentQueueDepth);
        Assert.AreEqual(1, map.For(first).ExplorationQueueDepth);
    }

    [TestMethod]
    public async Task SecondIoWaitsUntilFirstCompletes()
    {
        using var scheduler = new DeviceScheduler("PhysicalDiskUSB", 1, 256L * 1024 * 1024);
        using var first = await scheduler.AcquireIoAsync(8 * 1024 * 1024, CancellationToken.None);
        var secondTask = scheduler.AcquireIoAsync(8 * 1024 * 1024, CancellationToken.None).AsTask();
        Assert.IsFalse(secondTask.IsCompleted);
        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, scheduler.OutstandingIo);
        Assert.AreEqual(1, scheduler.PeakOutstandingIo);
    }

    [TestMethod]
    public void SnapshotStaysFixedInsteadOfExploring()
    {
        using var scheduler = new DeviceScheduler("PhysicalDisk", 16, 512L * 1024 * 1024);
        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(1, snapshot.InitialQueueDepth);
        Assert.AreEqual(1, snapshot.CurrentQueueDepth);
        Assert.AreEqual(1, snapshot.ExplorationQueueDepth);
        Assert.AreEqual(0, snapshot.QueueDepthUpshifts);
        Assert.AreEqual("fixed:extreme-style", snapshot.LastQueueDepthDecision);
    }

    [TestMethod]
    public void SoftBacklogTargetRemainsAccountingOnly()
    {
        const int block = 8 * 1024 * 1024;
        using var scheduler = new DeviceScheduler("PhysicalDisk", 1, block);
        scheduler.ReserveBacklog(block);
        scheduler.ReserveBacklog(block);
        Assert.AreEqual(2L * block, scheduler.Snapshot().QueuedBytes);
        scheduler.ReleaseBacklog(block);
        scheduler.ReleaseBacklog(block);
    }

    private static StorageDeviceInfo Device(string root, uint physicalDisk, string bus, StorageMediaKind media, bool? trim) =>
        new(root, Path.GetPathRoot(root)!, physicalDisk, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);
}
''', encoding='utf-8')

(root / 'dotnet/RepartoCopier.Core.Tests/StorageIoProfileTests.cs').write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class StorageIoProfileTests
{
    [TestMethod]
    public void AllLocalStorageProfilesUseFixedQueueDepthOne()
    {
        var profiles = new[]
        {
            StorageIoProfile.For(Device("USB", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("USB", StorageMediaKind.SolidState, false)),
            StorageIoProfile.For(Device("SATA", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("SATA", StorageMediaKind.Rotational, false)),
            StorageIoProfile.For(Device("NVMe", StorageMediaKind.SolidState, true)),
            StorageIoProfile.For(Device("Unknown", StorageMediaKind.Unknown, null)),
        };
        foreach (var profile in profiles) Assert.AreEqual(1, profile.InitialQueueDepth);
    }

    [TestMethod]
    public void UsbStillKeepsUsefulBacklogAccountingWithoutIncreasingIoConcurrency()
    {
        var usb = StorageIoProfile.For(Device("USB", StorageMediaKind.SolidState, true));
        Assert.AreEqual(StorageProfileKind.UsbSsd, usb.Kind);
        Assert.AreEqual(1, usb.InitialQueueDepth);
        Assert.AreEqual(256L * 1024 * 1024, usb.DeviceBacklogTargetBytes);
    }

    private static StorageDeviceInfo Device(string bus, StorageMediaKind media, bool? trim) =>
        new(@"C:\dest", @"C:\", 1, 1, bus, media, false, 512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, trim, 0);
}
''', encoding='utf-8')

# UI contract updated to the denser Windows 11 shell and logical completion rate.
(root / 'dotnet/RepartoCopier.Core.Tests/WinUiCompactProgressContractTests.cs').write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class WinUiCompactProgressContractTests
{
    [TestMethod]
    public void RunningViewUsesOneThickOverallProgressBarAndNoPerDiskRows()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(xaml.Contains("x:Name=\"OverallProgressBar\" Height=\"10\" MinHeight=\"10\"", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("x:Key=\"ProgressBarTrackHeight\">10", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProgressList", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_progressRows", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WindowUsesOfficialCompactDensityAndNativeTitleBar()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "App.xaml"));
        var xaml = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(app.Contains("Microsoft.UI.Xaml/DensityStyles/Compact.xaml", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("<TitleBar x:Name=\"AppTitleBar\"", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("SizeInt32(760, 400)", StringComparison.Ordinal));
        Assert.IsTrue(xaml.Contains("VerticalAlignment=\"Top\" Margin=\"0,10,0,0\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CopySpeedUsesTheSameLogicalCompletionProgressAsTheBar()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.WinUI", "MainWindow.xaml.cs"));
        Assert.IsTrue(code.Contains("_copyProgressRate.Observe(written)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("diagnostics.SourceRead5sBytesPerSecond", StringComparison.Ordinal));
        Assert.IsTrue(code.Contains("active.Min(item => item.Written)", StringComparison.Ordinal));
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
''', encoding='utf-8')

print('Extreme-style fixed I/O + compact WinUI migration applied')
