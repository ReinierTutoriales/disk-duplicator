$ErrorActionPreference = 'Stop'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText((Resolve-Path $Path)) }
function Write-Text([string]$Path, [string]$Content) { [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, [System.Text.UTF8Encoding]::new($false)) }
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if (-not $text.Contains($Old)) { throw "Expected anchor missing in $Path`n$Old" }
    Write-Text $Path ($text.Replace($Old, $New))
}

$classifier = @'
namespace RepartoCopier.Core;

/// <summary>
/// Classifies only failures for which retrying the same buffered write can
/// plausibly succeed without changing the request. Hardware/media errors such
/// as CRC (23) and I/O device error (1117) are deliberately not masked.
/// </summary>
internal static class TransientIoErrorClassifier
{
    internal static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
            return false;
        if (exception is not IOException io)
            return false;

        var code = io.HResult & 0xFFFF;
        return code is
            32 or   // ERROR_SHARING_VIOLATION
            33 or   // ERROR_LOCK_VIOLATION
            54 or   // ERROR_NETWORK_BUSY
            64 or   // ERROR_NETNAME_DELETED
            121 or  // ERROR_SEM_TIMEOUT
            1231 or // ERROR_NETWORK_UNREACHABLE
            1232 or // ERROR_HOST_UNREACHABLE
            1233 or // ERROR_PROTOCOL_UNREACHABLE
            1236 or // ERROR_CONNECTION_ABORTED
            1237;   // ERROR_RETRY
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'dotnet/RepartoCopier.Core/TransientIoErrorClassifier.cs'), $classifier, [System.Text.UTF8Encoding]::new($false))

$placement = @'
namespace RepartoCopier.Core;

internal static class BranchReplayPlacement
{
    internal static string? ResolveSafeDirectory(
        StorageDeviceInfo source,
        IReadOnlyList<StorageDeviceInfo> destinations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);

        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var tempDevice = StorageTopology.InspectDestinations([tempRoot]).Destinations.Single();
            if (!IsSafePhysicalPlacement(tempDevice, source, destinations))
                return null;
            return Path.Combine(tempRoot, "RepartoCopier", "branch-replay");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool IsSafePhysicalPlacement(
        StorageDeviceInfo replayDevice,
        StorageDeviceInfo source,
        IReadOnlyList<StorageDeviceInfo> destinations)
    {
        ArgumentNullException.ThrowIfNull(replayDevice);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);

        if (!StorageDeviceIdentity.TryGetExactPhysicalDeviceNumber(replayDevice, out var replayNumber) ||
            !StorageDeviceIdentity.TryGetExactPhysicalDeviceNumber(source, out var sourceNumber) ||
            replayNumber == sourceNumber)
        {
            return false;
        }

        foreach (var destination in destinations)
        {
            if (!StorageDeviceIdentity.TryGetExactPhysicalDeviceNumber(destination, out var destinationNumber) ||
                replayNumber == destinationNumber)
            {
                return false;
            }
        }

        return true;
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'dotnet/RepartoCopier.Core/BranchReplayPlacement.cs'), $placement, [System.Text.UTF8Encoding]::new($false))

$gate = @'
using System.Diagnostics;

namespace RepartoCopier.Core;

/// <summary>
/// Per-destination replay state. Entry and exit require sustained observations
/// and use different watermarks so a transient backlog spike cannot flap replay.
/// </summary>
internal sealed class BranchReplayGate
{
    internal static readonly TimeSpan DefaultEnterDuration = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan DefaultExitDuration = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _enterDuration;
    private readonly TimeSpan _exitDuration;
    private long _highSince;
    private long _lowSince;
    private bool _active;

    internal BranchReplayGate()
        : this(DefaultEnterDuration, DefaultExitDuration)
    {
    }

    internal BranchReplayGate(TimeSpan enterDuration, TimeSpan exitDuration)
    {
        if (enterDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(enterDuration));
        if (exitDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exitDuration));
        _enterDuration = enterDuration;
        _exitDuration = exitDuration;
    }

    internal bool IsActive => _active;

    internal bool ShouldReplay(long pendingBytes, long backlogTargetBytes, long timestamp)
    {
        if (backlogTargetBytes <= 0)
            return false;

        var enterThreshold = backlogTargetBytes;
        var exitThreshold = Math.Max(0L, backlogTargetBytes / 2);

        if (!_active)
        {
            _lowSince = 0;
            if (pendingBytes < enterThreshold)
            {
                _highSince = 0;
                return false;
            }

            if (_highSince == 0)
            {
                _highSince = timestamp;
                return _enterDuration == TimeSpan.Zero && Activate();
            }

            if (Stopwatch.GetElapsedTime(_highSince, timestamp) < _enterDuration)
                return false;

            return Activate();
        }

        _highSince = 0;
        if (pendingBytes > exitThreshold)
        {
            _lowSince = 0;
            return true;
        }

        if (_lowSince == 0)
        {
            _lowSince = timestamp;
            if (_exitDuration != TimeSpan.Zero)
                return true;
        }
        else if (Stopwatch.GetElapsedTime(_lowSince, timestamp) < _exitDuration)
        {
            return true;
        }

        _active = false;
        _lowSince = 0;
        return false;
    }

    private bool Activate()
    {
        _active = true;
        _highSince = 0;
        _lowSince = 0;
        return true;
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'dotnet/RepartoCopier.Core/BranchReplayGate.cs'), $gate, [System.Text.UTF8Encoding]::new($false))

$store = @'
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Append-only replay store for a persistently lagging destination branch.
/// Placement is supplied by BranchReplayPlacement; a null directory disables
/// disk replay rather than risking contention with source/destination devices.
/// </summary>
internal sealed class BranchReplayStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _directory;
    private FileStream? _stream;
    private long _nextOffset;
    private bool _disposed;

    internal BranchReplayStore(string? directory) => _directory = directory;

    internal bool IsEnabled => !string.IsNullOrWhiteSpace(_directory);

    internal readonly record struct Segment(long Offset, int Length, uint VerificationCrc32C);

    internal async ValueTask<Segment> SpillAsync(
        ReadOnlyMemory<byte> data,
        uint verificationCrc32,
        CancellationToken token)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Replay payload cannot be empty.", nameof(data));
        if (!IsEnabled)
            throw new InvalidOperationException("Replay store is disabled because no safe physical placement was proven.");

        SafeFileHandle handle;
        long offset;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream ??= OpenStore(_directory!);
            handle = _stream.SafeFileHandle;
            offset = _nextOffset;
            _nextOffset = checked(_nextOffset + data.Length);
        }

        await RandomAccess.WriteAsync(handle, data, offset, token).ConfigureAwait(false);
        return new Segment(offset, data.Length, verificationCrc32);
    }

    internal async ValueTask ReadAsync(
        Segment segment,
        Memory<byte> destination,
        CancellationToken token)
    {
        if (destination.Length < segment.Length)
            throw new ArgumentException("Replay destination is smaller than the segment.", nameof(destination));

        SafeFileHandle handle;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            handle = (_stream ?? throw new InvalidOperationException("Replay store has no data.")).SafeFileHandle;
        }

        var consumed = 0;
        while (consumed < segment.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                destination.Slice(consumed, segment.Length - consumed),
                checked(segment.Offset + consumed),
                token).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Replay store ended before the complete segment was read.");
            consumed += read;
        }
    }

    private static FileStream OpenStore(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.replay");
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            BufferSize = 1,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose,
        });
    }

    public void Dispose()
    {
        FileStream? stream;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            stream = _stream;
            _stream = null;
        }
        stream?.Dispose();
    }
}
'@
Write-Text 'dotnet/RepartoCopier.Core/BranchReplayStore.cs' $store

$enginePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$engine = Read-Text $enginePath

$oldWorkers = @'
            workers = copy.DestinationRoots
                .Select((root, index) => new DestinationWorker(
                    root,
                    index,
                    progress[index],
                    copy.DestinationDevices[index],
                    deviceSchedulers.For(copy.DestinationDevices[index]),
                    controlBudget))
                .ToArray();
'@
$newWorkers = @'
            var replayDirectory = BranchReplayPlacement.ResolveSafeDirectory(copy.SourceDevice, copy.DestinationDevices);
            workers = copy.DestinationRoots
                .Select((root, index) => new DestinationWorker(
                    root,
                    index,
                    progress[index],
                    copy.DestinationDevices[index],
                    deviceSchedulers.For(copy.DestinationDevices[index]),
                    controlBudget,
                    replayDirectory))
                .ToArray();
'@
if (-not $engine.Contains($oldWorkers)) { throw 'DestinationWorker construction anchor missing.' }
$engine = $engine.Replace($oldWorkers, $newWorkers)

$oldReplay = @'
                    FanoutMessage branchMessage = message;
                    if (recipients.Count > 1 &&
                        worker.PendingPayloadBytes >= Math.Max(0L, worker.DeviceScheduler.BacklogTargetBytes - message.Block.Length))
                    {
'@
$newReplay = @'
                    FanoutMessage branchMessage = message;
                    var shouldReplay = recipients.Count > 1 &&
                        worker.ReplayStore.IsEnabled &&
                        worker.ReplayGate.ShouldReplay(
                            worker.PendingPayloadBytes,
                            worker.DeviceScheduler.BacklogTargetBytes,
                            Stopwatch.GetTimestamp());
                    if (shouldReplay)
                    {
'@
if (-not $engine.Contains($oldReplay)) { throw 'Replay trigger anchor missing.' }
$engine = $engine.Replace($oldReplay, $newReplay)

$oldRetry = @'
                last = ex;
                if (ex is OperationCanceledException || attempt >= Retries)
                    break;
'@
$newRetry = @'
                last = ex;
                if (attempt >= Retries || !TransientIoErrorClassifier.IsTransient(ex))
                    break;
'@
if (-not $engine.Contains($oldRetry)) { throw 'Buffered retry anchor missing.' }
$engine = $engine.Replace($oldRetry, $newRetry)

$oldCtor = @'
            StorageDeviceInfo device,
            DeviceScheduler deviceScheduler,
            AdaptiveControlByteBudget controlBudget)
'@
$newCtor = @'
            StorageDeviceInfo device,
            DeviceScheduler deviceScheduler,
            AdaptiveControlByteBudget controlBudget,
            string? replayDirectory)
'@
if (-not $engine.Contains($oldCtor)) { throw 'DestinationWorker constructor signature anchor missing.' }
$engine = $engine.Replace($oldCtor, $newCtor)

$oldStore = @'
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            ReplayStore = new BranchReplayStore();
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
'@
$newStore = @'
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            ReplayStore = new BranchReplayStore(replayDirectory);
            ReplayGate = new BranchReplayGate();
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
'@
if (-not $engine.Contains($oldStore)) { throw 'ReplayStore construction anchor missing.' }
$engine = $engine.Replace($oldStore, $newStore)

$oldProp = '        internal BranchReplayStore ReplayStore { get; }'
$newProp = "        internal BranchReplayStore ReplayStore { get; }`r`n        internal BranchReplayGate ReplayGate { get; }"
if (-not $engine.Contains($oldProp)) { throw 'ReplayStore property anchor missing.' }
$engine = $engine.Replace($oldProp, $newProp)
Write-Text $enginePath $engine

$storeTests = @'
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchReplayStoreTests
{
    [TestMethod]
    public async Task ReplayStoreRoundTripsIndependentSegmentsAndCrcMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RepartoCopier-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new BranchReplayStore(directory);
            var first = new byte[257 * 1024 + 19];
            var second = new byte[513 * 1024 + 7];
            new Random(20260914).NextBytes(first);
            new Random(20260915).NextBytes(second);

            var firstCrc = FastCrc32C.Compute(first);
            var secondCrc = FastCrc32C.Compute(second);
            var firstSegment = await store.SpillAsync(first, firstCrc, CancellationToken.None);
            var secondSegment = await store.SpillAsync(second, secondCrc, CancellationToken.None);

            Assert.AreEqual(first.Length, firstSegment.Length);
            Assert.AreEqual(second.Length, secondSegment.Length);
            Assert.AreEqual(firstCrc, firstSegment.VerificationCrc32C);
            Assert.AreEqual(secondCrc, secondSegment.VerificationCrc32C);
            Assert.IsTrue(secondSegment.Offset >= firstSegment.Offset + firstSegment.Length);

            var firstRead = new byte[first.Length];
            var secondRead = new byte[second.Length];
            await store.ReadAsync(firstSegment, firstRead, CancellationToken.None);
            await store.ReadAsync(secondSegment, secondRead, CancellationToken.None);
            CollectionAssert.AreEqual(first, firstRead);
            CollectionAssert.AreEqual(second, secondRead);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task DisabledStoreRefusesDiskReplay()
    {
        using var store = new BranchReplayStore(null);
        Assert.IsFalse(store.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SpillAsync(new byte[4096], 0, CancellationToken.None));
    }
}
'@
Write-Text 'dotnet/RepartoCopier.Core.Tests/BranchReplayStoreTests.cs' $storeTests

$tests = @'
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class RetryAndReplayArchitectureTests
{
    [DataTestMethod]
    [DataRow(32, true)]
    [DataRow(33, true)]
    [DataRow(54, true)]
    [DataRow(64, true)]
    [DataRow(121, true)]
    [DataRow(1231, true)]
    [DataRow(1237, true)]
    [DataRow(3, false)]
    [DataRow(23, false)]
    [DataRow(1117, false)]
    public void TransientClassifierMatchesRetryContract(int win32Code, bool expected)
    {
        var error = new IOException("synthetic", unchecked((int)(0x80070000u | (uint)win32Code)));
        Assert.AreEqual(expected, TransientIoErrorClassifier.IsTransient(error));
    }

    [TestMethod]
    public void CancellationAndNonIoFailuresAreNeverTransient()
    {
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(new OperationCanceledException()));
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(new UnauthorizedAccessException()));
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(new InvalidOperationException()));
    }

    [TestMethod]
    public void BufferedProductionWriteActuallyCallsTransientClassifier()
    {
        var copyEngine = typeof(CopyEngine);
        var method = copyEngine.GetMethod("WriteBlockAtOffsetAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("WriteBlockAtOffsetAsync no existe.");
        var target = typeof(TransientIoErrorClassifier).GetMethod("IsTransient", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("TransientIoErrorClassifier.IsTransient no existe.");
        var il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new AssertFailedException("WriteBlockAtOffsetAsync no expone IL.");
        var token = BitConverter.GetBytes(target.MetadataToken);
        var found = false;
        for (var i = 0; i <= il.Length - token.Length; i++)
        {
            if (il.AsSpan(i, token.Length).SequenceEqual(token))
            {
                found = true;
                break;
            }
        }
        Assert.IsTrue(found, "La ruta buffered productiva debe consumir TransientIoErrorClassifier.IsTransient; no basta con que el clasificador exista.");
    }

    [TestMethod]
    public void ReplayPlacementRequiresExactDifferentPhysicalDeviceFromEveryParticipant()
    {
        var source = Device("C:\\source", 1);
        var destinations = new[] { Device("D:\\dest", 2), Device("E:\\dest", 3) };
        Assert.IsTrue(BranchReplayPlacement.IsSafePhysicalPlacement(Device("F:\\temp", 4), source, destinations));
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(Device("C:\\temp", 1), source, destinations));
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(Device("D:\\temp", 2), source, destinations));

        var unknownTemp = Device("F:\\temp", null);
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(unknownTemp, source, destinations));
        var unknownDestination = new[] { Device("D:\\dest", null) };
        Assert.IsFalse(BranchReplayPlacement.IsSafePhysicalPlacement(Device("F:\\temp", 4), source, unknownDestination));
    }

    [TestMethod]
    public void ReplayGateRequiresSustainedHighBacklogAndHystereticLowExit()
    {
        var gate = new BranchReplayGate(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var frequency = Stopwatch.Frequency;
        static long At(long origin, TimeSpan elapsed) => origin + (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
        var t0 = frequency;

        Assert.IsFalse(gate.ShouldReplay(1000, 1000, t0));
        Assert.IsFalse(gate.ShouldReplay(1000, 1000, At(t0, TimeSpan.FromMilliseconds(50))));
        Assert.IsTrue(gate.ShouldReplay(1000, 1000, At(t0, TimeSpan.FromMilliseconds(110))));
        Assert.IsTrue(gate.IsActive);

        Assert.IsTrue(gate.ShouldReplay(400, 1000, At(t0, TimeSpan.FromMilliseconds(120))));
        Assert.IsTrue(gate.ShouldReplay(400, 1000, At(t0, TimeSpan.FromMilliseconds(180))));
        Assert.IsFalse(gate.ShouldReplay(400, 1000, At(t0, TimeSpan.FromMilliseconds(230))));
        Assert.IsFalse(gate.IsActive);
    }

    [TestMethod]
    public void ReplayGateRejectsOneSampleSpike()
    {
        var gate = new BranchReplayGate(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var t0 = Stopwatch.Frequency;
        Assert.IsFalse(gate.ShouldReplay(1000, 1000, t0));
        Assert.IsFalse(gate.ShouldReplay(100, 1000, t0 + Stopwatch.Frequency / 20));
        Assert.IsFalse(gate.ShouldReplay(1000, 1000, t0 + Stopwatch.Frequency / 10));
        Assert.IsFalse(gate.IsActive);
    }

    private static StorageDeviceInfo Device(string root, uint? physicalDeviceNumber) =>
        new(
            root,
            Path.GetPathRoot(root) ?? root,
            physicalDeviceNumber,
            1,
            "Synthetic",
            StorageMediaKind.SolidState,
            false,
            4096,
            4096,
            physicalDeviceNumber.HasValue,
            null);
}
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'dotnet/RepartoCopier.Core.Tests/RetryAndReplayArchitectureTests.cs'), $tests, [System.Text.UTF8Encoding]::new($false))

# Keep roadmap honest about what is now actually wired in production.
$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'
$roadmap = Read-Text $roadmapPath
$append = @'

### Cierre de auditoría — retry y replay por rama

- Buffered retry consulta `TransientIoErrorClassifier.IsTransient` desde `WriteBlockAtOffsetAsync`; errores permanentes y fallos de medio (incluidos Win32 23/1117) no consumen reintentos.
- Replay a disco solo se habilita cuando el volumen temporal tiene identidad física `Exact` y se demuestra distinto del origen y de todos los destinos activos. Sin esa prueba, replay queda deshabilitado de forma conservadora.
- La entrada/salida de replay usa histéresis temporal por rama: backlog alto sostenido para entrar y backlog por debajo del 50% sostenido para salir. Un pico aislado no activa spool.
- Contratos de arquitectura verifican el consumidor productivo del clasificador, la colocación física y la histéresis; `IOException` de spill conserva el fallback correcto al `SharedBlock` en memoria.
'@
if (-not $roadmap.Contains('### Cierre de auditoría — retry y replay por rama')) {
    $roadmap += $append
    Write-Text $roadmapPath $roadmap
}

dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal
git diff --check
Write-Host 'Retry classification and replay safety patch applied.'
