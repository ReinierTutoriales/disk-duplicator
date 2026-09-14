$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce([string]$text, [string]$old, [string]$new, [string]$label) {
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -ne 1) { throw "$label expected exactly one match, found $count." }
    return $text.Replace($old, $new)
}

$coreProjectPath = 'dotnet/RepartoCopier.Core/RepartoCopier.Core.csproj'
$enginePath = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$contractPath = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
$coreProject = Get-Content -Raw $coreProjectPath
$engine = Get-Content -Raw $enginePath
$telemetry = Get-Content -Raw $telemetryPath
$contract = Get-Content -Raw $contractPath

$coreProject = Replace-ExactlyOnce $coreProject '<AllowUnsafeBlocks>false</AllowUnsafeBlocks>' '<AllowUnsafeBlocks>true</AllowUnsafeBlocks>' 'unsafe setting'

$spoolStore = @'
using System.Buffers;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

/// <summary>
/// Temporary backing store used only when real FAN-OUT memory pressure and
/// relative branch lag coincide. The store is eligible only on a physical
/// device proven independent from both source and every destination.
/// </summary>
internal sealed class FanoutSpoolStore : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private readonly FileStream _stream;
    private readonly DeviceScheduler _scheduler;
    private long _nextOffset;
    private int _enabled = 1;

    private FanoutSpoolStore(string directory, string path, FileStream stream, StorageDeviceInfo device)
    {
        _directory = directory;
        _path = path;
        _stream = stream;
        Device = device;
        var profile = StorageIoProfile.For(device);
        _scheduler = new DeviceScheduler(
            $"Spool:{device.PhysicalDeviceId}",
            profile.InitialQueueDepth,
            profile.DeviceBacklogTargetBytes,
            StorageDeviceIdentity.ConfidenceFor(device));
    }

    internal StorageDeviceInfo Device { get; }
    internal DeviceScheduler Scheduler => _scheduler;
    internal bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    internal static bool TryCreate(
        StorageDeviceInfo source,
        IReadOnlyList<StorageDeviceInfo> destinations,
        out FanoutSpoolStore? store)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);
        store = null;
        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            Directory.CreateDirectory(tempRoot);
            var spoolDevice = StorageTopology.InspectDestinations([tempRoot]).Destinations.Single();
            if (!IsProvenIndependent(spoolDevice, source, destinations))
                return false;

            var directory = Path.Combine(tempRoot, $"RepartoCopier-Spool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "fanout.spool");
            var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
                BufferSize = 1,
            });
            store = new FanoutSpoolStore(directory, path, stream, spoolDevice);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            store = null;
            return false;
        }
    }

    internal static bool IsProvenIndependent(
        StorageDeviceInfo spool,
        StorageDeviceInfo source,
        IReadOnlyList<StorageDeviceInfo> destinations)
    {
        if (!spool.ProbeSucceeded || spool.IsNetwork || spool.PhysicalDeviceNumber is not uint spoolNumber)
            return false;
        if (!source.ProbeSucceeded || source.PhysicalDeviceNumber is not uint sourceNumber || sourceNumber == spoolNumber)
            return false;
        foreach (var destination in destinations)
        {
            if (!destination.ProbeSucceeded ||
                destination.PhysicalDeviceNumber is not uint destinationNumber ||
                destinationNumber == spoolNumber)
                return false;
        }
        return true;
    }

    internal void Disable() => Interlocked.Exchange(ref _enabled, 0);

    internal async ValueTask<SpoolSegment> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (!IsEnabled)
            throw new IOException("El spool FAN-OUT fue deshabilitado para este trabajo.");
        if (data.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(data));

        var offset = ReserveSegment(data.Length);
        using var io = await _scheduler.AcquireIoAsync(data.Length, token).ConfigureAwait(false);
        await RandomAccess.WriteAsync(_stream.SafeFileHandle, data, offset, token).ConfigureAwait(false);
        return new SpoolSegment(_path, offset, data.Length);
    }

    private long ReserveSegment(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        while (true)
        {
            var current = Interlocked.Read(ref _nextOffset);
            var aligned = AlignUp(current, DirectIoSourceReader.MaximumSupportedAlignment);
            var next = checked(aligned + length);
            if (Interlocked.CompareExchange(ref _nextOffset, next, current) == current)
                return aligned;
        }
    }

    private static long AlignUp(long value, int alignment)
    {
        var mask = alignment - 1L;
        return checked((value + mask) & ~mask);
    }

    public ValueTask DisposeAsync()
    {
        Disable();
        _scheduler.Dispose();
        _stream.Dispose();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
        return ValueTask.CompletedTask;
    }

    internal readonly record struct SpoolSegment(string Path, long Offset, int Length)
    {
        internal MappedPayload OpenMapped() => new(Path, Offset, Length);
    }

    internal sealed unsafe class MappedPayload : MemoryManager<byte>
    {
        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _view;
        private byte* _pointer;
        private readonly int _length;

        internal MappedPayload(string path, long offset, int length)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            _length = length;
            _mapping = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _mapping.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _pointer = pointer + checked((int)_view.PointerOffset);
        }

        internal bool IsAlignedFor(int alignment) =>
            alignment <= 1 || ((nuint)_pointer % (nuint)alignment) == 0;

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_view is null, this);
            return new Span<byte>(_pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(_view is null, this);
            if ((uint)elementIndex > (uint)_length)
                throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle(_pointer + elementIndex, default, this);
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            var view = Interlocked.Exchange(ref _view, null);
            if (view is not null)
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                view.Dispose();
            }
            Interlocked.Exchange(ref _mapping, null)?.Dispose();
            _pointer = null;
        }
    }
}
'@
Set-Content 'dotnet/RepartoCopier.Core/FanoutSpoolStore.cs' $spoolStore -NoNewline

$spillPolicy = @'
namespace RepartoCopier.Core;

internal static class FanoutSpillPolicy
{
    /// <summary>
    /// Selects only the relatively most-lagging branches, and only when the
    /// shared-memory budget cannot immediately admit another source block.
    /// No byte, ratio, time, device-class, or destination-count threshold is used.
    /// </summary>
    internal static int[] SelectLaggingSlots(
        IReadOnlyList<long> logicalPendingBytes,
        bool memoryCanAdmitNextBlock,
        bool spoolAvailable)
    {
        ArgumentNullException.ThrowIfNull(logicalPendingBytes);
        if (!spoolAvailable || memoryCanAdmitNextBlock || logicalPendingBytes.Count < 2)
            return [];

        var minimum = logicalPendingBytes.Min();
        var maximum = logicalPendingBytes.Max();
        if (maximum <= minimum)
            return [];

        return logicalPendingBytes
            .Select((pending, slot) => (pending, slot))
            .Where(item => item.pending == maximum)
            .Select(item => item.slot)
            .ToArray();
    }
}
'@
Set-Content 'dotnet/RepartoCopier.Core/FanoutSpillPolicy.cs' $spillPolicy -NoNewline

# Telemetry: measurable spill/write/replay/fallback behavior.
$telemetry = Replace-ExactlyOnce $telemetry @'
    public long PeakVerificationReadBytes { get; init; }

    private static double Rate'@ @'
    public long PeakVerificationReadBytes { get; init; }
    public long FanoutSpoolWriteBytes { get; init; }
    public long FanoutSpoolReplayBytes { get; init; }
    public long FanoutSpilledBranches { get; init; }
    public int FanoutSpoolFallbacks { get; init; }

    private static double Rate'@ 'spool snapshot properties'
$telemetry = Replace-ExactlyOnce $telemetry @'
    private long _verificationReadBudgetBytes, _peakVerificationReadBytes;
    private long _peakBufferedBytes, _maxObservedBufferTargetBytes;
'@ @'
    private long _verificationReadBudgetBytes, _peakVerificationReadBytes;
    private long _fanoutSpoolWriteBytes, _fanoutSpoolReplayBytes, _fanoutSpilledBranches;
    private int _fanoutSpoolFallbacks;
    private long _peakBufferedBytes, _maxObservedBufferTargetBytes;
'@ 'spool telemetry fields'
$telemetry = Replace-ExactlyOnce $telemetry @'
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);

    internal void RecordWrite'@ @'
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);
    internal void RecordFanoutSpoolWrite(int bytes) { if (bytes > 0) Interlocked.Add(ref _fanoutSpoolWriteBytes, bytes); }
    internal void RecordFanoutSpoolReplay(int bytes) { if (bytes > 0) Interlocked.Add(ref _fanoutSpoolReplayBytes, bytes); }
    internal void RecordFanoutSpilledBranch() => Interlocked.Increment(ref _fanoutSpilledBranches);
    internal void RecordFanoutSpoolFallback() => Interlocked.Increment(ref _fanoutSpoolFallbacks);

    internal void RecordWrite'@ 'spool telemetry methods'
$telemetry = Replace-ExactlyOnce $telemetry @'
            VerificationReadBudgetBytes = Interlocked.Read(ref _verificationReadBudgetBytes),
            PeakVerificationReadBytes = Interlocked.Read(ref _peakVerificationReadBytes),
        };'@ @'
            VerificationReadBudgetBytes = Interlocked.Read(ref _verificationReadBudgetBytes),
            PeakVerificationReadBytes = Interlocked.Read(ref _peakVerificationReadBytes),
            FanoutSpoolWriteBytes = Interlocked.Read(ref _fanoutSpoolWriteBytes),
            FanoutSpoolReplayBytes = Interlocked.Read(ref _fanoutSpoolReplayBytes),
            FanoutSpilledBranches = Interlocked.Read(ref _fanoutSpilledBranches),
            FanoutSpoolFallbacks = Volatile.Read(ref _fanoutSpoolFallbacks),
        };'@ 'spool telemetry snapshot wiring'

# RunAsync: optional proven-independent spool, included in scheduler telemetry.
$engine = Replace-ExactlyOnce $engine @'
        var pipeline = new PipelineGovernor(bufferBudget, BlockSize);
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
        using var deviceSchedulers = DeviceSchedulerMap.Create(copy.SourceDevice, copy.DestinationDevices);
        job.Telemetry.AttachDeviceSchedulers(deviceSchedulers.Schedulers);
        try
'@ @'
        var pipeline = new PipelineGovernor(bufferBudget, BlockSize);
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
        using var deviceSchedulers = DeviceSchedulerMap.Create(copy.SourceDevice, copy.DestinationDevices);
        FanoutSpoolStore.TryCreate(copy.SourceDevice, copy.DestinationDevices, out var spool);
        var diagnosticSchedulers = spool is null
            ? deviceSchedulers.Schedulers
            : deviceSchedulers.Schedulers.Concat([spool.Scheduler]).ToArray();
        job.Telemetry.AttachDeviceSchedulers(diagnosticSchedulers);
        try
'@ 'run spool creation'
$engine = Replace-ExactlyOnce $engine @'
                    pipeline,
                    bufferBudget,
                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);
'@ @'
                    pipeline,
                    bufferBudget,
                    spool,
                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);
'@ 'producer spool argument'
$engine = Replace-ExactlyOnce $engine @'
        finally
        {
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
            }
        }
    }

    private static async Task ProducerLoopAsync('@ @'
        finally
        {
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
            }
            if (spool is not null)
                await spool.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ProducerLoopAsync('@ 'spool cleanup'
$engine = Replace-ExactlyOnce $engine @'
        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget,
        DeviceScheduler? sharedSourceScheduler)
'@ @'
        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget,
        FanoutSpoolStore? spool,
        DeviceScheduler? sharedSourceScheduler)
'@ 'producer signature'

# Control delivery no longer goes through a generic data/control multiplexer.
$engine = $engine.Replace('await DeliverAsync(active, new BeginMessage(entry), job).ConfigureAwait(false);', 'await DeliverControlGroupAsync(active, new BeginMessage(entry), job).ConfigureAwait(false);')
$engine = $engine.Replace('await DeliverAsync(active, new EndMessage(hash), job).ConfigureAwait(false);', 'await DeliverControlGroupAsync(active, new EndMessage(hash), job).ConfigureAwait(false);')

# Pass spool/budget into both data delivery sites.
$engine = Replace-ExactlyOnce $engine @'
                    await DeliverAsync(active, new DataMessage(block), job).ConfigureAwait(false);
'@ @'
                    await DeliverDataAsync(active, new DataMessage(block), job, bufferBudget, spool).ConfigureAwait(false);
'@ 'sequential data delivery'
$engine = Replace-ExactlyOnce $engine @'
                await DeliverAsync(active, new DataMessage(shared), job).ConfigureAwait(false);
'@ @'
                await DeliverDataAsync(active, new DataMessage(shared), job, bufferBudget, spool).ConfigureAwait(false);
'@ 'prefetch data delivery'

# Replace generic DeliverAsync + old DeliverDataAsync with explicit control group and spill-aware data route.
$deliverStart = $engine.IndexOf('    private static async Task DeliverAsync(')
$deliverEnd = $engine.IndexOf('    private static async ValueTask DeliverControlAsync(', $deliverStart)
if ($deliverStart -lt 0 -or $deliverEnd -lt 0) { throw 'Could not locate delivery methods.' }
$newDelivery = @'
    private static async Task DeliverControlGroupAsync(
        IReadOnlyList<DestinationWorker> recipients,
        ControlMessage message,
        CopyJob job)
    {
        for (var index = 0; index < recipients.Count; index++)
            await DeliverControlAsync(recipients[index], message, job).ConfigureAwait(false);
    }

    private static async Task DeliverDataAsync(
        IReadOnlyList<DestinationWorker> recipients,
        DataMessage message,
        CopyJob job,
        AdaptiveByteBudget bufferBudget,
        FanoutSpoolStore? spool)
    {
        if (recipients.Count == 0)
            return;

        var logicalLag = recipients
            .Select(worker => worker.LogicalPendingBytes)
            .ToArray();
        var spillSlots = FanoutSpillPolicy.SelectLaggingSlots(
            logicalLag,
            bufferBudget.CanAdmitImmediately(message.Block.ReservedBytes),
            spool?.IsEnabled == true);
        var spillSet = spillSlots.ToHashSet();
        FanoutSpoolStore.SpoolSegment? segment = null;
        if (spillSet.Count > 0 && spool is not null)
        {
            try
            {
                segment = await spool.WriteAsync(message.Block.Memory, job.Token).ConfigureAwait(false);
                job.Telemetry.RecordFanoutSpoolWrite(message.Block.Length);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                spool.Disable();
                job.Telemetry.RecordFanoutSpoolFallback();
                spillSet.Clear();
            }
        }

        var index = -1;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            for (index = 0; index < recipients.Count; index++)
            {
                var worker = recipients[index];
                if (!worker.IsActive)
                {
                    message.Block.Release();
                    continue;
                }

                var useSpool = segment.HasValue && spillSet.Contains(index);
                var backlogOwned = false;
                var pendingPayloadOwned = false;
                var deferredSpoolOwned = false;
                var queueOwned = false;
                var blockOwned = true;
                try
                {
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    backlogOwned = true;
                    if (useSpool)
                    {
                        worker.ReserveDeferredSpool(message.Block.Length);
                        deferredSpoolOwned = true;
                    }
                    else
                    {
                        worker.ReservePendingPayload(message.Block.Length);
                        pendingPayloadOwned = true;
                    }

                    if (!worker.IsActive)
                    {
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                        backlogOwned = false;
                        if (pendingPayloadOwned)
                        {
                            worker.ReleasePendingPayload(message.Block.Length);
                            pendingPayloadOwned = false;
                        }
                        if (deferredSpoolOwned)
                        {
                            worker.ReleaseDeferredSpool(message.Block.Length);
                            deferredSpoolOwned = false;
                        }
                        message.Block.Release();
                        blockOwned = false;
                        continue;
                    }

                    worker.IncrementQueueDepth();
                    queueOwned = true;
                    FanoutMessage delivery = useSpool
                        ? new SpoolDataMessage(segment!.Value, message.Block.Length, message.Block.VerificationCrc32)
                        : message;
                    if (worker.Channel.Writer.TryWrite(delivery))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        if (useSpool)
                        {
                            deferredSpoolOwned = false;
                            message.Block.Release();
                            blockOwned = false;
                            job.Telemetry.RecordFanoutSpilledBranch();
                        }
                        else
                        {
                            pendingPayloadOwned = false;
                            blockOwned = false;
                        }
                        continue;
                    }

                    worker.DecrementQueueDepth();
                    queueOwned = false;
                    worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    backlogOwned = false;
                    if (pendingPayloadOwned)
                    {
                        worker.ReleasePendingPayload(message.Block.Length);
                        pendingPayloadOwned = false;
                    }
                    if (deferredSpoolOwned)
                    {
                        worker.ReleaseDeferredSpool(message.Block.Length);
                        deferredSpoolOwned = false;
                    }
                    message.Block.Release();
                    blockOwned = false;
                    if (worker.IsActive)
                        worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
                }
                catch
                {
                    if (queueOwned)
                        worker.DecrementQueueDepth();
                    if (backlogOwned)
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    if (pendingPayloadOwned)
                        worker.ReleasePendingPayload(message.Block.Length);
                    if (deferredSpoolOwned)
                        worker.ReleaseDeferredSpool(message.Block.Length);
                    if (blockOwned)
                        message.Block.Release();
                    throw;
                }
            }
        }
        catch
        {
            for (var remaining = index + 1; remaining < recipients.Count; remaining++)
                message.Block.Release();
            throw;
        }
    }

'@
$engine = $engine.Substring(0, $deliverStart) + $newDelivery + $engine.Substring($deliverEnd)

# Writer loop supports both shared-RAM and deferred spool payloads.
$engine = Replace-ExactlyOnce $engine @'
                var data = effectiveMessage as DataMessage;
                var dataOwnedByWriter = data is not null;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
'@ @'
                var data = effectiveMessage as DataMessage;
                var spoolData = effectiveMessage as SpoolDataMessage;
                var dataOwnedByWriter = data is not null;
                var spoolOwnedByWriter = spoolData is not null;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                else if (spoolData is not null)
                    worker.DeviceScheduler.ReleaseBacklog(spoolData.Length);
'@ 'writer message ownership'
$engine = Replace-ExactlyOnce $engine @'
                            current.PendingWrites.Add(
                                WriteBlockAtOffsetAsync(worker, current, chunkData.Block, offset, job));
                            dataOwnedByWriter = false;
                            PruneCompletedSuccesses(current);
                            break;

                        case DataMessage:
                            break;

                        case EndMessage end when current is not null:
'@ @'
                            var payload = new SharedWritePayload(worker, chunkData.Block);
                            current.PendingWrites.Add(
                                WritePayloadAtOffsetAsync(worker, current, payload, offset, job));
                            dataOwnedByWriter = false;
                            PruneCompletedSuccesses(current);
                            break;

                        case SpoolDataMessage deferred when current is not null:
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

                            var spoolOffset = current.ReserveWriteOffset(deferred.Length);
                            current.VerificationBlocks.Add(new VerificationBlock(deferred.Length, deferred.VerificationCrc32));
                            current.PendingWrites.Add(
                                WriteSpoolSegmentAtOffsetAsync(worker, current, deferred, spoolOffset, job));
                            spoolOwnedByWriter = false;
                            PruneCompletedSuccesses(current);
                            break;

                        case DataMessage:
                        case SpoolDataMessage:
                            break;

                        case EndMessage end when current is not null:
'@ 'writer spool scheduling'
$engine = Replace-ExactlyOnce $engine @'
                    if (dataOwnedByWriter && data is not null)
                        ReleaseBranchBlock(worker, data.Block);
                    controlDelivery?.ReleaseBudget();
'@ @'
                    if (dataOwnedByWriter && data is not null)
                        ReleaseBranchBlock(worker, data.Block);
                    if (spoolOwnedByWriter && spoolData is not null)
                        worker.ReleaseDeferredSpool(spoolData.Length);
                    controlDelivery?.ReleaseBudget();
'@ 'writer spool unscheduled cleanup'

# Replace SharedBlock-only writer with common payload writer + spool adapter.
$writeStart = $engine.IndexOf('    private static async Task<PendingWriteResult> WriteBlockAtOffsetAsync(')
$writeEnd = $engine.IndexOf('    private static void PruneCompletedSuccesses(', $writeStart)
if ($writeStart -lt 0 -or $writeEnd -lt 0) { throw 'Could not locate old write method.' }
$newWriter = @'
    private static async Task<PendingWriteResult> WriteSpoolSegmentAtOffsetAsync(
        DestinationWorker worker,
        CurrentFile current,
        SpoolDataMessage deferred,
        long offset,
        CopyJob job)
    {
        try
        {
            var mapped = deferred.Segment.OpenMapped();
            var payload = new SpoolWritePayload(worker, mapped, deferred.Length);
            return await WritePayloadAtOffsetAsync(worker, current, payload, offset, job).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            worker.ReleaseDeferredSpool(deferred.Length);
            return PendingWriteResult.Failed(ex);
        }
    }

    private static async Task<PendingWriteResult> WritePayloadAtOffsetAsync(
        DestinationWorker worker,
        CurrentFile current,
        IWritePayload payload,
        long offset,
        CopyJob job)
    {
        var data = payload.Memory;
        Exception? last = null;
        var direct = current.DirectSession;

        if (direct is not null)
        {
            try
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                if (!payload.IsAlignedFor(direct.Alignment))
                {
                    var alignmentError = new InvalidOperationException("El payload FAN-OUT no conserva la alineación requerida por Direct I/O.");
                    current.RequestDirectFallback();
                    return PendingWriteResult.NeedsBufferedRetry(payload, offset, alignmentError);
                }

                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                int operations;
                try
                {
                    operations = await direct.WriteAsync(
                        data,
                        offset,
                        current.Entry.Size,
                        payloadIsAligned: true,
                        queueDepth,
                        StorageWritePolicy.MinimumParallelSliceBytes,
                        worker.DeviceScheduler,
                        job.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (DirectIoDestinationWriter.IsFallbackable(ex))
                {
                    current.RequestDirectFallback();
                    return PendingWriteResult.NeedsBufferedRetry(payload, offset, ex);
                }

                job.Telemetry.RecordDirectDestinationWrite(data.Length, operations);
                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started));
                if (payload.IsSpool)
                    job.Telemetry.RecordFanoutSpoolReplay(data.Length);
                current.RecordCompletedWrite(data.Length);
                worker.Progress.AddWritten(data.Length);
                worker.NoteProgress();
                payload.Dispose();
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                payload.Dispose();
                return PendingWriteResult.Failed(ex);
            }
        }

        for (var attempt = 0; attempt <= Retries; attempt++)
        {
            try
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                var stream = current.Stream ?? throw new IOException($"No existe handle buffered para {current.Entry.RelativePath}.");
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                var operations = await DestinationWriteCoordinator.WriteAsync(
                    stream.SafeFileHandle,
                    data,
                    offset,
                    queueDepth,
                    StorageWritePolicy.MinimumParallelSliceBytes,
                    worker.DeviceScheduler,
                    job.Token).ConfigureAwait(false);

                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started));
                if (payload.IsSpool)
                    job.Telemetry.RecordFanoutSpoolReplay(data.Length);
                current.RecordCompletedWrite(data.Length);
                worker.Progress.AddWritten(data.Length);
                worker.NoteProgress();
                payload.Dispose();
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                last = ex;
                if (ex is OperationCanceledException || attempt >= Retries)
                    break;
                worker.Progress.AddRetry();
                try
                {
                    await Task.Delay(75 * (attempt + 1), job.Token).ConfigureAwait(false);
                }
                catch (Exception delayError)
                {
                    last = delayError;
                    break;
                }
            }
        }

        payload.Dispose();
        return PendingWriteResult.Failed(
            new IOException($"No se pudo escribir {current.Entry.RelativePath} en offset {offset} después de reintentos.", last));
    }

'@
$engine = $engine.Substring(0, $writeStart) + $newWriter + $engine.Substring($writeEnd)

# Retry/fallback carries unified payload rather than SharedBlock.
$engine = $engine.Replace('retry.RetryBlock', 'retry.RetryPayload')
$engine = $engine.Replace('result.RetryBlock', 'result.RetryPayload')
$engine = Replace-ExactlyOnce $engine @'
            return WriteBlockAtOffsetAsync(
                worker,
                current,
                result.RetryPayload ?? throw new InvalidOperationException("Fallback sin bloque retenido."),
                result.Offset,
                job);
'@ @'
            return WritePayloadAtOffsetAsync(
                worker,
                current,
                result.RetryPayload ?? throw new InvalidOperationException("Fallback sin payload retenido."),
                result.Offset,
                job);
'@ 'fallback unified writer'
$engine = Replace-ExactlyOnce $engine @'
    private static void ReleaseRetryBlock(DestinationWorker worker, SharedBlock? block)
    {
        if (block is not null)
            ReleaseBranchBlock(worker, block);
    }
'@ @'
    private static void ReleaseRetryPayload(IWritePayload? payload) => payload?.Dispose();
'@ 'retry payload cleanup helper'
$engine = $engine.Replace('ReleaseRetryBlock(worker, retry.RetryPayload)', 'ReleaseRetryPayload(retry.RetryPayload)')
$engine = $engine.Replace('ReleaseRetryBlock(worker, result.RetryPayload)', 'ReleaseRetryPayload(result.RetryPayload)')

# Messages and SharedBlock expose spool metadata/pressure size.
$engine = Replace-ExactlyOnce $engine @'
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;
'@ @'
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record SpoolDataMessage(
        FanoutSpoolStore.SpoolSegment Segment,
        int Length,
        uint VerificationCrc32) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;
'@ 'spool data message'
$engine = Replace-ExactlyOnce $engine @'
        public int Length { get; }
        public uint VerificationCrc32 { get; }
'@ @'
        public int Length { get; }
        internal int ReservedBytes => _reservedBytes;
        public uint VerificationCrc32 { get; }
'@ 'shared reserved bytes'

# Budget exposes real safe-memory admission, not current target heuristics.
$engine = Replace-ExactlyOnce $engine @'
        internal int GetAdmissibleConcurrency(int bytesPerBlock)
'@ @'
        internal bool CanAdmitImmediately(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            lock (_gate)
                return _usedBytes + bytes <= CurrentSafeCapacityLocked();
        }

        internal int GetAdmissibleConcurrency(int bytesPerBlock)
'@ 'safe immediate admission'

# Destination worker tracks RAM-held + deferred-spool logical lag.
$engine = Replace-ExactlyOnce $engine @'
        private long _pendingPayloadBytes;
        private long _peakPendingPayloadBytes;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;
'@ @'
        private long _pendingPayloadBytes;
        private long _peakPendingPayloadBytes;
        private long _deferredSpoolBytes;
        private long _peakDeferredSpoolBytes;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;
'@ 'deferred spool fields'
$engine = Replace-ExactlyOnce $engine @'
        public long PendingPayloadBytes => Interlocked.Read(ref _pendingPayloadBytes);
        public long PeakPendingPayloadBytes => Interlocked.Read(ref _peakPendingPayloadBytes);
        public DateTime LastProgressUtc'@ @'
        public long PendingPayloadBytes => Interlocked.Read(ref _pendingPayloadBytes);
        public long PeakPendingPayloadBytes => Interlocked.Read(ref _peakPendingPayloadBytes);
        public long DeferredSpoolBytes => Interlocked.Read(ref _deferredSpoolBytes);
        public long PeakDeferredSpoolBytes => Interlocked.Read(ref _peakDeferredSpoolBytes);
        public long LogicalPendingBytes => checked(PendingPayloadBytes + DeferredSpoolBytes);
        public DateTime LastProgressUtc'@ 'deferred spool properties'
$engine = Replace-ExactlyOnce $engine @'
        public void IncrementQueueDepth()
'@ @'
        public void ReserveDeferredSpool(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            var pending = Interlocked.Add(ref _deferredSpoolBytes, bytes);
            var peak = Interlocked.Read(ref _peakDeferredSpoolBytes);
            while (pending > peak)
            {
                var observed = Interlocked.CompareExchange(ref _peakDeferredSpoolBytes, pending, peak);
                if (observed == peak)
                    break;
                peak = observed;
            }
        }

        public void ReleaseDeferredSpool(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            while (true)
            {
                var current = Interlocked.Read(ref _deferredSpoolBytes);
                if (current < bytes)
                    throw new InvalidOperationException("La rama intentó liberar más payload diferido del que mantiene en spool.");
                if (Interlocked.CompareExchange(ref _deferredSpoolBytes, current - bytes, current) == current)
                    return;
            }
        }

        public void IncrementQueueDepth()
'@ 'deferred spool accounting methods'

# Queue drain handles both payload forms.
$engine = Replace-ExactlyOnce $engine @'
            if (message is DataMessage data)
            {
                worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                ReleaseBranchBlock(worker, data.Block);
            }
            else
            {
                ReleaseQueuedControl(message);
            }
'@ @'
            switch (message)
            {
                case DataMessage data:
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                    ReleaseBranchBlock(worker, data.Block);
                    break;
                case SpoolDataMessage deferred:
                    worker.DeviceScheduler.ReleaseBacklog(deferred.Length);
                    worker.ReleaseDeferredSpool(deferred.Length);
                    break;
                default:
                    ReleaseQueuedControl(message);
                    break;
            }
'@ 'drain spool messages'

# Unified write payload ownership classes + retry result.
$payloadAnchor = '    private enum PendingWriteStatus'
$payloadIndex = $engine.IndexOf($payloadAnchor)
if ($payloadIndex -lt 0) { throw 'PendingWriteStatus anchor missing.' }
$payloadTypes = @'
    private interface IWritePayload : IDisposable
    {
        ReadOnlyMemory<byte> Memory { get; }
        int Length { get; }
        bool IsSpool { get; }
        bool IsAlignedFor(int alignment);
    }

    private sealed class SharedWritePayload(DestinationWorker worker, SharedBlock block) : IWritePayload
    {
        private DestinationWorker? _worker = worker;
        private SharedBlock? _block = block;
        public ReadOnlyMemory<byte> Memory => (_block ?? throw new ObjectDisposedException(nameof(SharedWritePayload))).Memory;
        public int Length => (_block ?? throw new ObjectDisposedException(nameof(SharedWritePayload))).Length;
        public bool IsSpool => false;
        public bool IsAlignedFor(int alignment) => (_block ?? throw new ObjectDisposedException(nameof(SharedWritePayload))).IsAlignedFor(alignment);
        public void Dispose()
        {
            var ownedBlock = Interlocked.Exchange(ref _block, null);
            var ownedWorker = Interlocked.Exchange(ref _worker, null);
            if (ownedBlock is not null && ownedWorker is not null)
                ReleaseBranchBlock(ownedWorker, ownedBlock);
        }
    }

    private sealed class SpoolWritePayload : IWritePayload
    {
        private DestinationWorker? _worker;
        private FanoutSpoolStore.MappedPayload? _mapped;
        private readonly int _length;
        internal SpoolWritePayload(DestinationWorker worker, FanoutSpoolStore.MappedPayload mapped, int length)
        {
            _worker = worker;
            _mapped = mapped;
            _length = length;
        }
        public ReadOnlyMemory<byte> Memory => (_mapped ?? throw new ObjectDisposedException(nameof(SpoolWritePayload))).Memory[.._length];
        public int Length => _length;
        public bool IsSpool => true;
        public bool IsAlignedFor(int alignment) => (_mapped ?? throw new ObjectDisposedException(nameof(SpoolWritePayload))).IsAlignedFor(alignment);
        public void Dispose()
        {
            Interlocked.Exchange(ref _mapped, null)?.Dispose();
            var worker = Interlocked.Exchange(ref _worker, null);
            worker?.ReleaseDeferredSpool(_length);
        }
    }

'@
$engine = $engine.Insert($payloadIndex, $payloadTypes)
$engine = Replace-ExactlyOnce $engine @'
    private sealed record PendingWriteResult(
        PendingWriteStatus Status,
        SharedBlock? RetryBlock,
        long Offset,
        Exception? Error)
'@ @'
    private sealed record PendingWriteResult(
        PendingWriteStatus Status,
        IWritePayload? RetryPayload,
        long Offset,
        Exception? Error)
'@ 'retry payload result type'
$engine = Replace-ExactlyOnce $engine @'
        internal static PendingWriteResult NeedsBufferedRetry(SharedBlock block, long offset, Exception error) =>
            new(PendingWriteStatus.NeedsBufferedRetry, block, offset, error);
'@ @'
        internal static PendingWriteResult NeedsBufferedRetry(IWritePayload payload, long offset, Exception error) =>
            new(PendingWriteStatus.NeedsBufferedRetry, payload, offset, error);
'@ 'retry payload factory'

# Remove old helper naming if replacement left it.
if ($engine -match 'WriteBlockAtOffsetAsync|RetryBlock|ReleaseRetryBlock|private static async Task DeliverAsync\(') {
    throw 'Superseded shared-only delivery/write route remains after spool migration.'
}

# Product tests for selection + physical placement + mapped replay exactness.
$spoolTests = @'
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FanoutSpoolTests
{
    [TestMethod]
    public void SpillPolicyUsesRealPressureAndRelativeLagWithoutStaticThreshold()
    {
        CollectionAssert.AreEqual(Array.Empty<int>(),
            FanoutSpillPolicy.SelectLaggingSlots([0L, 500L, 100L], memoryCanAdmitNextBlock: true, spoolAvailable: true));
        CollectionAssert.AreEqual(Array.Empty<int>(),
            FanoutSpillPolicy.SelectLaggingSlots([500L, 500L, 500L], memoryCanAdmitNextBlock: false, spoolAvailable: true));
        CollectionAssert.AreEqual(new[] { 1, 2 },
            FanoutSpillPolicy.SelectLaggingSlots([0L, 500L, 500L], memoryCanAdmitNextBlock: false, spoolAvailable: true));
    }

    [TestMethod]
    public void SpoolRequiresProvenIndependentPhysicalDevice()
    {
        var source = Device("C:\\", 1);
        var a = Device("E:\\", 2);
        var b = Device("F:\\", 3);
        var spool = Device("D:\\", 9);
        Assert.IsTrue(FanoutSpoolStore.IsProvenIndependent(spool, source, [a, b]));
        Assert.IsFalse(FanoutSpoolStore.IsProvenIndependent(Device("D:\\", 1), source, [a, b]));
        Assert.IsFalse(FanoutSpoolStore.IsProvenIndependent(Device("D:\\", 2), source, [a, b]));
    }

    private static StorageDeviceInfo Device(string root, uint physicalDisk) =>
        new(root, root, physicalDisk, 1, "NVMe", StorageMediaKind.SolidState, false,
            512, 4096, true, null, false, "NTFS", DriveType.Fixed, false, true, true, 0);
}
'@
Set-Content 'dotnet/RepartoCopier.Core.Tests/FanoutSpoolTests.cs' $spoolTests -NoNewline

# Architecture contract follows the single unified writer.
$contract = $contract.Replace('CollectionAssert.Contains(engineMethods, "WriteBlockAtOffsetAsync");', 'CollectionAssert.DoesNotContain(engineMethods, "WriteBlockAtOffsetAsync");`n        CollectionAssert.Contains(engineMethods, "WritePayloadAtOffsetAsync");`n        CollectionAssert.Contains(engineMethods, "WriteSpoolSegmentAtOffsetAsync");')
$contract = Replace-ExactlyOnce $contract @'
        CollectionAssert.Contains(engineMethods, "ReleaseBranchBlock");
        CollectionAssert.Contains(engineMethods, "ReleaseQueuedControl");
'@ @'
        CollectionAssert.Contains(engineMethods, "ReleaseBranchBlock");
        CollectionAssert.Contains(engineMethods, "ReleaseQueuedControl");
        CollectionAssert.Contains(engineMethods, "WritePayloadAtOffsetAsync");
        CollectionAssert.Contains(engineMethods, "WriteSpoolSegmentAtOffsetAsync");
'@ 'spool unified writer contract'

Set-Content $coreProjectPath $coreProject -NoNewline
Set-Content $enginePath $engine -NoNewline
Set-Content $telemetryPath $telemetry -NoNewline
Set-Content $contractPath $contract -NoNewline
