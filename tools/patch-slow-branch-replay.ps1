$ErrorActionPreference = 'Stop'

$core = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$testPath = 'dotnet/RepartoCopier.Core.Tests/BranchReplayStoreTests.cs'
$storePath = 'dotnet/RepartoCopier.Core/BranchReplayStore.cs'
$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'

@'
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Append-only temporary replay store used only by a destination branch that has
/// exceeded its soft physical backlog watermark. It breaks the branch's ownership
/// of source SharedBlock memory while preserving one physical source read.
/// The file is created lazily on the system temporary volume and is delete-on-close.
/// </summary>
internal sealed class BranchReplayStore : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _stream;
    private long _nextOffset;
    private bool _disposed;

    internal readonly record struct Segment(long Offset, int Length, uint VerificationCrc32);

    internal async ValueTask<Segment> SpillAsync(
        ReadOnlyMemory<byte> data,
        uint verificationCrc32,
        CancellationToken token)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Replay payload cannot be empty.", nameof(data));

        SafeFileHandle handle;
        long offset;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream ??= OpenStore();
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

    private static FileStream OpenStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RepartoCopier", "branch-replay");
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
'@ | Set-Content -LiteralPath $storePath -Encoding utf8

@'
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchReplayStoreTests
{
    [TestMethod]
    public async Task ReplayStoreRoundTripsIndependentSegmentsAndCrcMetadata()
    {
        using var store = new BranchReplayStore();
        var first = new byte[257 * 1024 + 19];
        var second = new byte[513 * 1024 + 7];
        new Random(20260914).NextBytes(first);
        new Random(20260915).NextBytes(second);

        var firstCrc = FastCrc32.Compute(first);
        var secondCrc = FastCrc32.Compute(second);
        var firstSegment = await store.SpillAsync(first, firstCrc, CancellationToken.None);
        var secondSegment = await store.SpillAsync(second, secondCrc, CancellationToken.None);

        Assert.AreEqual(first.Length, firstSegment.Length);
        Assert.AreEqual(second.Length, secondSegment.Length);
        Assert.AreEqual(firstCrc, firstSegment.VerificationCrc32);
        Assert.AreEqual(secondCrc, secondSegment.VerificationCrc32);
        Assert.IsTrue(secondSegment.Offset >= firstSegment.Offset + firstSegment.Length);

        var firstRead = new byte[first.Length];
        var secondRead = new byte[second.Length];
        await store.ReadAsync(firstSegment, firstRead, CancellationToken.None);
        await store.ReadAsync(secondSegment, secondRead, CancellationToken.None);
        CollectionAssert.AreEqual(first, firstRead);
        CollectionAssert.AreEqual(second, secondRead);
    }
}
'@ | Set-Content -LiteralPath $testPath -Encoding utf8

$copy = Get-Content -LiteralPath $core -Raw

$oldDispose = @'
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
            }
'@
$newDispose = @'
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
                worker.Dispose();
            }
'@
if (-not $copy.Contains($oldDispose)) { throw 'RunAsync cleanup anchor not found.' }
$copy = $copy.Replace($oldDispose, $newDispose)

$oldDeliveryWrite = @'
                    worker.IncrementQueueDepth();
                    queueOwned = true;
                    if (worker.Channel.Writer.TryWrite(message))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        pendingPayloadOwned = false;
                        blockOwned = false;
                        continue;
                    }
'@
$newDeliveryWrite = @'
                    FanoutMessage branchMessage = message;
                    if (recipients.Count > 1 &&
                        worker.PendingPayloadBytes >= Math.Max(0L, worker.DeviceScheduler.BacklogTargetBytes - message.Block.Length))
                    {
                        try
                        {
                            var replayStarted = Stopwatch.GetTimestamp();
                            var segment = await worker.ReplayStore.SpillAsync(
                                message.Block.Memory,
                                message.Block.VerificationCrc32,
                                job.Token).ConfigureAwait(false);
                            job.Telemetry.RecordBranchReplayWrite(
                                segment.Length,
                                Stopwatch.GetElapsedTime(replayStarted));
                            message.Block.Release();
                            blockOwned = false;
                            branchMessage = new ReplayDataMessage(segment);
                        }
                        catch (IOException)
                        {
                            // Replay is an optimization. If the temporary volume cannot
                            // accept it, preserve correctness by keeping the shared block.
                        }
                    }

                    worker.IncrementQueueDepth();
                    queueOwned = true;
                    if (worker.Channel.Writer.TryWrite(branchMessage))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        pendingPayloadOwned = false;
                        blockOwned = false;
                        continue;
                    }
'@
if (-not $copy.Contains($oldDeliveryWrite)) { throw 'DeliverData write anchor not found.' }
$copy = $copy.Replace($oldDeliveryWrite, $newDeliveryWrite)

$oldWriterHeader = @'
                var data = effectiveMessage as DataMessage;
                var dataOwnedByWriter = data is not null;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
'@
$newWriterHeader = @'
                var data = effectiveMessage as DataMessage;
                var replay = effectiveMessage as ReplayDataMessage;
                var dataOwnedByWriter = data is not null;
                var replayOwnedByWriter = replay is not null;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                else if (replay is not null)
                    worker.DeviceScheduler.ReleaseBacklog(replay.Segment.Length);
'@
if (-not $copy.Contains($oldWriterHeader)) { throw 'Writer header anchor not found.' }
$copy = $copy.Replace($oldWriterHeader, $newWriterHeader)

$oldDataCases = @'
                        case DataMessage:
                            break;

                        case EndMessage end when current is not null:
'@
$newDataCases = @'
                        case ReplayDataMessage replayData when current is not null:
                            if (current.Failed)
                                break;

                            if (current.DirectFallbackRequested)
                            {
                                var fallbackError = await DrainPendingWritesAsync(worker, current, job).ConfigureAwait(false);
                                if (fallbackError is not null)
                                {
                                    FailCurrentFile(worker, current, options, fallbackError.Message);
                                    break;
                                }
                            }

                            var replayOffset = current.ReserveWriteOffset(replayData.Segment.Length);
                            current.VerificationBlocks.Add(
                                new VerificationBlock(replayData.Segment.Length, replayData.Segment.VerificationCrc32));
                            current.PendingWrites.Add(
                                WriteReplayBlockAtOffsetAsync(worker, current, replayData.Segment, replayOffset, job));
                            replayOwnedByWriter = false;
                            PruneCompletedSuccesses(current);
                            break;

                        case DataMessage:
                        case ReplayDataMessage:
                            break;

                        case EndMessage end when current is not null:
'@
if (-not $copy.Contains($oldDataCases)) { throw 'Writer data cases anchor not found.' }
$copy = $copy.Replace($oldDataCases, $newDataCases)

$oldWriterFinally = @'
                    if (dataOwnedByWriter && data is not null)
                        ReleaseBranchBlock(worker, data.Block);
                    controlDelivery?.ReleaseBudget();
'@
$newWriterFinally = @'
                    if (dataOwnedByWriter && data is not null)
                        ReleaseBranchBlock(worker, data.Block);
                    if (replayOwnedByWriter && replay is not null)
                        worker.ReleasePendingPayload(replay.Segment.Length);
                    controlDelivery?.ReleaseBudget();
'@
if (-not $copy.Contains($oldWriterFinally)) { throw 'Writer finally anchor not found.' }
$copy = $copy.Replace($oldWriterFinally, $newWriterFinally)

$writeAnchor = '    private static async Task<PendingWriteResult> WriteBlockAtOffsetAsync('
$writeIndex = $copy.IndexOf($writeAnchor)
if ($writeIndex -lt 0) { throw 'WriteBlockAtOffsetAsync anchor not found.' }
$replayMethod = @'
    private static async Task<PendingWriteResult> WriteReplayBlockAtOffsetAsync(
        DestinationWorker worker,
        CurrentFile current,
        BranchReplayStore.Segment segment,
        long offset,
        CopyJob job)
    {
        SourceBufferLease? lease = SourceBufferLease.RentAligned(
            segment.Length,
            DirectIoSourceReader.MaximumSupportedAlignment);
        try
        {
            var replayStarted = Stopwatch.GetTimestamp();
            await worker.ReplayStore.ReadAsync(
                segment,
                lease.Memory[..segment.Length],
                job.Token).ConfigureAwait(false);
            job.Telemetry.RecordBranchReplayRead(
                segment.Length,
                Stopwatch.GetElapsedTime(replayStarted));
            var block = new SharedBlock(lease, segment.Length, segment.VerificationCrc32);
            lease = null;
            return await WriteBlockAtOffsetAsync(worker, current, block, offset, job).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lease?.Dispose();
            worker.ReleasePendingPayload(segment.Length);
            return PendingWriteResult.Failed(ex);
        }
    }

'@
$copy = $copy.Insert($writeIndex, $replayMethod)

$oldDrain = @'
            if (message is DataMessage data)
            {
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                ReleaseBranchBlock(worker, data.Block);
            }
            else
            {
                ReleaseQueuedControl(message);
            }
'@
$newDrain = @'
            if (message is DataMessage data)
            {
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                ReleaseBranchBlock(worker, data.Block);
            }
            else if (message is ReplayDataMessage replay)
            {
                worker.DeviceScheduler.ReleaseBacklog(replay.Segment.Length);
                worker.ReleasePendingPayload(replay.Segment.Length);
            }
            else
            {
                ReleaseQueuedControl(message);
            }
'@
if (-not $copy.Contains($oldDrain)) { throw 'DrainAndRelease anchor not found.' }
$copy = $copy.Replace($oldDrain, $newDrain)

$oldMessages = @'
    private sealed record BeginMessage(FileEntry Entry) : ControlMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;
'@
$newMessages = @'
    private sealed record BeginMessage(FileEntry Entry) : ControlMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record ReplayDataMessage(BranchReplayStore.Segment Segment) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;
'@
if (-not $copy.Contains($oldMessages)) { throw 'Fanout message anchor not found.' }
$copy = $copy.Replace($oldMessages, $newMessages)

$oldSharedFields = @'
        private readonly int _reservedBytes;
        private readonly AdaptiveByteBudget _budget;
'@
$newSharedFields = @'
        private readonly int _reservedBytes;
        private readonly AdaptiveByteBudget? _budget;
'@
# Only replace the occurrence in SharedBlock; SourceReadBlock must retain non-null budget.
$sharedStart = $copy.IndexOf('    internal sealed class SharedBlock')
if ($sharedStart -lt 0) { throw 'SharedBlock class not found.' }
$fieldIndex = $copy.IndexOf($oldSharedFields, $sharedStart)
if ($fieldIndex -lt 0) { throw 'SharedBlock fields not found.' }
$copy = $copy.Remove($fieldIndex, $oldSharedFields.Length).Insert($fieldIndex, $newSharedFields)

$oldSharedCtor = @'
        internal SharedBlock(SourceBufferLease buffer, int length, int reservedBytes, int references, AdaptiveByteBudget budget)
        {
            _buffer = buffer;
            Length = length;
            VerificationCrc32 = FastCrc32.Compute(buffer.Memory.Span[..length]);
            _reservedBytes = reservedBytes;
            _references = references;
            _budget = budget;
        }
'@
$newSharedCtor = @'
        internal SharedBlock(SourceBufferLease buffer, int length, int reservedBytes, int references, AdaptiveByteBudget budget)
        {
            _buffer = buffer;
            Length = length;
            VerificationCrc32 = FastCrc32.Compute(buffer.Memory.Span[..length]);
            _reservedBytes = reservedBytes;
            _references = references;
            _budget = budget;
        }

        internal SharedBlock(SourceBufferLease buffer, int length, uint verificationCrc32)
        {
            _buffer = buffer;
            Length = length;
            VerificationCrc32 = verificationCrc32;
            _reservedBytes = 0;
            _references = 1;
            _budget = null;
        }
'@
if (-not $copy.Contains($oldSharedCtor)) { throw 'SharedBlock ctor anchor not found.' }
$copy = $copy.Replace($oldSharedCtor, $newSharedCtor)
$copy = $copy.Replace('            _budget.Release(_reservedBytes);', '            if (_budget is not null && _reservedBytes > 0)`r`n                _budget.Release(_reservedBytes);')
# Undo accidental replacement in SourceReadBlock if present.
$copy = $copy.Replace('            if (_budget is not null && _reservedBytes > 0)`r`n                _budget.Release(_reservedBytes);`r`n        }`r`n    }`r`n`r`n    private abstract record FanoutMessage;', '            _budget.Release(_reservedBytes);`r`n        }`r`n    }`r`n`r`n    private abstract record FanoutMessage;')

$oldWorkerClass = '    private sealed class DestinationWorker'
$newWorkerClass = '    private sealed class DestinationWorker : IDisposable'
if (-not $copy.Contains($oldWorkerClass)) { throw 'DestinationWorker class anchor not found.' }
$copy = $copy.Replace($oldWorkerClass, $newWorkerClass)

$oldWorkerAssign = @'
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
'@
$newWorkerAssign = @'
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            ReplayStore = new BranchReplayStore();
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
'@
if (-not $copy.Contains($oldWorkerAssign)) { throw 'DestinationWorker constructor anchor not found.' }
$copy = $copy.Replace($oldWorkerAssign, $newWorkerAssign)

$oldWorkerProps = @'
        internal AdaptiveControlByteBudget ControlBudget { get; }
        public Channel<FanoutMessage> Channel { get; }
'@
$newWorkerProps = @'
        internal AdaptiveControlByteBudget ControlBudget { get; }
        internal BranchReplayStore ReplayStore { get; }
        public Channel<FanoutMessage> Channel { get; }
'@
if (-not $copy.Contains($oldWorkerProps)) { throw 'DestinationWorker property anchor not found.' }
$copy = $copy.Replace($oldWorkerProps, $newWorkerProps)

$oldWorkerFail = @'
        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
        }
'@
$newWorkerFail = @'
        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
        }

        public void Dispose() => ReplayStore.Dispose();
'@
if (-not $copy.Contains($oldWorkerFail)) { throw 'DestinationWorker Fail anchor not found.' }
$copy = $copy.Replace($oldWorkerFail, $newWorkerFail)

Set-Content -LiteralPath $core -Value $copy -Encoding utf8

$telemetry = Get-Content -LiteralPath $telemetryPath -Raw
$telemetry = $telemetry.Replace(
'    public long PeakVerificationReadBytes { get; init; }',
'    public long PeakVerificationReadBytes { get; init; }`r`n    public long BranchReplayWriteBytes { get; init; }`r`n    public TimeSpan BranchReplayWriteTime { get; init; }`r`n    public long BranchReplayReadBytes { get; init; }`r`n    public TimeSpan BranchReplayReadTime { get; init; }`r`n    public long BranchReplaySegments { get; init; }')
$telemetry = $telemetry.Replace(
'    private long _verificationReadBudgetBytes, _peakVerificationReadBytes;',
'    private long _verificationReadBudgetBytes, _peakVerificationReadBytes;`r`n    private long _branchReplayWriteBytes, _branchReplayWriteTicks;`r`n    private long _branchReplayReadBytes, _branchReplayReadTicks, _branchReplaySegments;')
$telemetry = $telemetry.Replace(
'    internal void RecordCopyPhase(TimeSpan elapsed) => AddTicks(ref _copyPhaseTicks, elapsed);',
'    internal void RecordBranchReplayWrite(int bytes, TimeSpan elapsed) { AddBytes(ref _branchReplayWriteBytes, bytes); AddTicks(ref _branchReplayWriteTicks, elapsed); Interlocked.Increment(ref _branchReplaySegments); }`r`n    internal void RecordBranchReplayRead(int bytes, TimeSpan elapsed) { AddBytes(ref _branchReplayReadBytes, bytes); AddTicks(ref _branchReplayReadTicks, elapsed); }`r`n    internal void RecordCopyPhase(TimeSpan elapsed) => AddTicks(ref _copyPhaseTicks, elapsed);')
$telemetry = $telemetry.Replace(
'            PeakVerificationReadBytes = Interlocked.Read(ref _peakVerificationReadBytes),',
'            PeakVerificationReadBytes = Interlocked.Read(ref _peakVerificationReadBytes),`r`n            BranchReplayWriteBytes = Interlocked.Read(ref _branchReplayWriteBytes),`r`n            BranchReplayWriteTime = ToTimeSpan(Interlocked.Read(ref _branchReplayWriteTicks)),`r`n            BranchReplayReadBytes = Interlocked.Read(ref _branchReplayReadBytes),`r`n            BranchReplayReadTime = ToTimeSpan(Interlocked.Read(ref _branchReplayReadTicks)),`r`n            BranchReplaySegments = Interlocked.Read(ref _branchReplaySegments),')
Set-Content -LiteralPath $telemetryPath -Value $telemetry -Encoding utf8

$contract = Get-Content -LiteralPath $contractPath -Raw
$contractAnchor = @'
    [TestMethod]
    public void DestinationBranchTracksPayloadUntilItsSharedReferenceIsActuallyReleased()
'@
$contractInsert = @'
    [TestMethod]
    public void SlowBranchReplayIsARealProductionPath()
    {
        var assembly = typeof(CopyEngine).Assembly;
        Assert.IsNotNull(assembly.GetType("RepartoCopier.Core.BranchReplayStore"));
        var replayMessage = typeof(CopyEngine).GetNestedType("ReplayDataMessage", BindingFlags.NonPublic);
        Assert.IsNotNull(replayMessage);
        var worker = typeof(CopyEngine).GetNestedType("DestinationWorker", BindingFlags.NonPublic);
        Assert.IsNotNull(worker);
        Assert.IsNotNull(worker.GetProperty("ReplayStore", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(engineMethods, "WriteReplayBlockAtOffsetAsync");
    }

'@
if (-not $contract.Contains($contractAnchor)) { throw 'Unification insertion anchor not found.' }
$contract = $contract.Replace($contractAnchor, $contractInsert + $contractAnchor)
Set-Content -LiteralPath $contractPath -Value $contract -Encoding utf8

$roadmap = Get-Content -LiteralPath $roadmapPath -Raw
$oldSlow = @'
### P1 — slow-branch decoupling

Una rama permanentemente más lenta conserva referencias a `SharedBlock` durante más tiempo. Mientras exista headroom de RAM esto no afecta a las ramas rápidas; bajo presión sostenida puede terminar frenando al productor. Diseñar desacoplamiento por rama que preserve una sola lectura física del source: ventana dinámica por destino, batching/deferred write y, si el benchmark lo justifica, spill/replay para la rama atrasada. No resolverlo limitando todas las ramas a la velocidad del destino lento.

'@
$newSlow = @'
### VALIDACIÓN FÍSICA — slow-branch decoupling

Integrado replay por rama: cuando una rama supera su `BacklogTargetBytes` y existen múltiples destinos, el payload se deriva a un `BranchReplayStore` temporal append-only, se libera inmediatamente la referencia de esa rama al `SharedBlock` y el writer la reproduce después conservando CRC32C, offsets y una sola lectura física del source. El replay es best-effort: si el volumen temporal no puede aceptarlo se conserva la ruta shared normal. Telemetría expone bytes/tiempo/segmentos de replay. Pendiente únicamente medir en hardware real el punto de activación y el coste del volumen temporal.

'@
if (-not $roadmap.Contains($oldSlow)) { throw 'Slow branch roadmap block not found.' }
$roadmap = $roadmap.Replace($oldSlow, $newSlow)
Set-Content -LiteralPath $roadmapPath -Value $roadmap -Encoding utf8

git diff --check
git status --short
