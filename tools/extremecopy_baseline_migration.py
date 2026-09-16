from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / 'dotnet/RepartoCopier.Core'
TESTS = ROOT / 'dotnet/RepartoCopier.Core.Tests'
UI = ROOT / 'dotnet/RepartoCopier.WinUI'
ENGINE = CORE / 'CopyEngine.cs'
MODELS = CORE / 'Models.cs'
TELEMETRY = CORE / 'CopyTelemetry.cs'
MAIN = UI / 'MainWindow.xaml.cs'
README = ROOT / 'README.md'
CHANGELOG = ROOT / 'CHANGELOG.md'


def replace_once(text, old, new, label):
    n = text.count(old)
    if n != 1:
        raise RuntimeError(f'{label}: expected one match, got {n}')
    return text.replace(old, new, 1)


def cut_between(text, start_marker, end_marker, replacement, label):
    start = text.find(start_marker)
    if start < 0:
        raise RuntimeError(f'{label}: start marker missing')
    end = text.find(end_marker, start)
    if end < 0:
        raise RuntimeError(f'{label}: end marker missing')
    return text[:start] + replacement + text[end:]

engine = ENGINE.read_text(encoding='utf-8')

# Fixed, deliberately small shared pool. One source read is referenced by every destination.
engine = replace_once(engine,
    '    private const long InitialBufferBudget = 512L * 1024 * 1024;\n',
    '    private const int SharedFanoutBlockBytes = 8 * 1024 * 1024;\n    private const long SharedFanoutPoolBytes = 64L * 1024 * 1024;\n',
    'shared pool constants')

# RunAsync: remove source pipeline governor + replay/staging. One queue per destination.
engine = replace_once(engine,
'''        var bufferBudget = AdaptiveByteBudget.CreateForSystem();
        var controlBudget = AdaptiveControlByteBudget.CreateForSystem();
        var pipeline = new PipelineGovernor(bufferBudget, Math.Max(1, Environment.SystemPageSize));
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
        using var deviceSchedulers = DeviceSchedulerMap.Create(copy.SourceDevice, copy.DestinationDevices);
''',
'''        var bufferBudget = new AdaptiveByteBudget(SharedFanoutPoolBytes, SharedFanoutPoolBytes);
        var controlBudget = AdaptiveControlByteBudget.CreateForSystem();
        using var deviceSchedulers = DeviceSchedulerMap.Create(copy.SourceDevice, copy.DestinationDevices);
''',
'run fixed shared pool')

start = engine.index('            var replayDirectory = options.EnableReplay')
end = engine.index('            var copyPhaseStarted = Stopwatch.GetTimestamp();', start)
engine = engine[:start] + '''            workers = copy.DestinationRoots
                .Select((root, index) => new DestinationWorker(
                    root,
                    index,
                    progress[index],
                    copy.DestinationDevices[index],
                    deviceSchedulers.For(copy.DestinationDevices[index]),
                    controlBudget))
                .ToArray();

''' + engine[end:]

engine = replace_once(engine,
'''            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job))
                .ToArray();
            var stagingTasks = workers
                .Select(worker => StageBranchAsync(worker, job))
                .ToArray();
''',
'''            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job))
                .ToArray();
''',
'remove staging tasks')

engine = replace_once(engine,
'''                    pipeline,
                    bufferBudget,
                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);
''',
'''                    bufferBudget,
                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);
''',
'producer args')

engine = replace_once(engine,
'''            finally
            {
                foreach (var worker in workers)
                    worker.Ingress.Writer.TryComplete(producerError);
            }

            Exception? writerError = null;
            try
            {
                await Task.WhenAll(stagingTasks).ConfigureAwait(false);
                await Task.WhenAll(writerTasks).ConfigureAwait(false);
            }
''',
'''            finally
            {
                foreach (var worker in workers)
                    worker.Channel.Writer.TryComplete(producerError);
            }

            Exception? writerError = null;
            try
            {
                await Task.WhenAll(writerTasks).ConfigureAwait(false);
            }
''',
'complete one queue')

engine = replace_once(engine,
'''            foreach (var worker in workers)
            {
                worker.Ingress.Writer.TryComplete();
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker.Ingress.Reader, worker);
                DrainAndRelease(worker.Channel.Reader, worker);
                worker.Dispose();
            }
''',
'''            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker.Channel.Reader, worker);
            }
''',
'final one queue')

# Producer: no adaptive transfer-size model/prefetch pipeline. Fixed aligned shared chunks, natural pool backpressure.
engine = replace_once(engine,
'''        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget,
        DeviceScheduler? sharedSourceScheduler)
''',
'''        CopyJob job,
        AdaptiveByteBudget bufferBudget,
        DeviceScheduler? sharedSourceScheduler)
''',
'producer signature')

old = '''                var transferAlignment = TransferAlignmentFor(copy.SourceDevice, active);
                var signals = active
                    .Select(worker => worker.DeviceScheduler.Snapshot())
                    .Select(snapshot => new TransferDeviceSignal(
                        snapshot.CurrentQueueDepth,
                        snapshot.BestObservedQueueDepth,
                        snapshot.BestObservedThroughputBytesPerSecond,
                        snapshot.BestObservedAverageLatencyMilliseconds))
                    .ToArray();
                var readBufferSize = AdaptiveTransferSizer.Select(
                    entry.Size,
                    active.Count,
                    transferAlignment,
                    bufferBudget.TargetBytes,
                    bufferBudget.UsedBytes,
                    Math.Max(1, pipeline.Snapshot().CurrentPrefetchLimit),
                    signals);
                pipeline.SetBytesPerBlock(readBufferSize);
                job.Telemetry.RecordTransferSize(readBufferSize);

                var sourceResult = entry.Size > readBufferSize
                    ? await ReadAndFanOutPrefetchedAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        readBufferSize,
                        transferAlignment,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        readBufferSize,
                        transferAlignment,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false);
'''
new = '''                var transferAlignment = TransferAlignmentFor(copy.SourceDevice, active);
                var readBufferSize = SelectSharedFanoutBlockSize(entry.Size, transferAlignment);
                job.Telemetry.RecordTransferSize(readBufferSize);

                var sourceResult = await ReadAndFanOutSequentialAsync(
                    entry,
                    copy.SourceDevice,
                    active,
                    readBufferSize,
                    transferAlignment,
                    bufferBudget,
                    job,
                    sharedSourceScheduler).ConfigureAwait(false);
'''
engine = replace_once(engine, old, new, 'simple producer read path')

# Simplify sequential source read method: remove governor timings but preserve telemetry.
engine = replace_once(engine,
'''        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
''',
'''        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        DeviceScheduler? sharedSourceScheduler)
''',
'sequential signature')
engine = engine.replace('                pipeline.RecordBudgetWait(budgetElapsed);\n', '', 1)
engine = engine.replace('                    pipeline.RecordSourceRead(readElapsed);\n', '', 1)
engine = engine.replace('                    pipeline.RecordDeliveryWait(deliveryElapsed);\n', '', 1)

# Delete the entire prefetch/hash queue pipeline.
engine = cut_between(
    engine,
    '    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(',
    '    private static FileStream OpenSourceStream(string path) =>',
    '',
    'remove prefetch pipeline')

# Direct one-to-N delivery: same SharedBlock pointer/lease into each destination queue.
start = engine.index('    private static async Task DeliverDataAsync(')
end = engine.index('    private static async ValueTask DeliverControlAsync(', start)
engine = engine[:start] + '''    private static async Task DeliverDataAsync(
        IReadOnlyList<DestinationWorker> recipients,
        DataMessage message,
        CopyJob job)
    {
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

                var payloadOwned = false;
                var queueOwned = false;
                var blockOwned = true;
                try
                {
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    worker.ReservePendingPayload(message.Block.Length);
                    payloadOwned = true;
                    worker.IncrementQueueDepth();
                    queueOwned = true;

                    if (worker.Channel.Writer.TryWrite(message))
                    {
                        payloadOwned = false;
                        queueOwned = false;
                        blockOwned = false;
                        continue;
                    }

                    worker.DecrementQueueDepth();
                    queueOwned = false;
                    ReleaseBranchPayload(worker, message.Block.Length);
                    payloadOwned = false;
                    message.Block.Release();
                    blockOwned = false;
                    if (worker.IsActive)
                        worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
                }
                catch
                {
                    if (queueOwned)
                        worker.DecrementQueueDepth();
                    if (payloadOwned)
                        ReleaseBranchPayload(worker, message.Block.Length);
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

''' + engine[end:]

# Control messages also go directly to the only destination queue.
engine = engine.replace('worker.Ingress.Writer.TryWrite(delivery)', 'worker.Channel.Writer.TryWrite(delivery)')
engine = engine.replace('El canal de entrada del destino se cerró antes de recibir todos los datos.', 'El canal del destino se cerró antes de recibir todos los datos.')

# Writer owns queue-depth dequeue, has no replay message path.
engine = replace_once(engine,
'''            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                var controlDelivery = message as ControlDelivery;
''',
'''            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                worker.DecrementQueueDepth();
                var controlDelivery = message as ControlDelivery;
''',
'writer queue accounting')
engine = engine.replace('                var replay = effectiveMessage as ReplayDataMessage;\n', '')
engine = engine.replace('                var replayOwnedByWriter = replay is not null;\n', '')

# Remove ReplayDataMessage switch case from writer.
case_start = engine.find('                        case ReplayDataMessage replayData when current is not null:')
if case_start >= 0:
    case_end = engine.find('                        case DataMessage:', case_start)
    if case_end < 0:
        raise RuntimeError('replay writer case end missing')
    engine = engine[:case_start] + engine[case_end:]
engine = engine.replace('                        case ReplayDataMessage:\n', '')
engine = engine.replace('                    if (replayOwnedByWriter && replay is not null)\n                        ReleaseBranchPayload(worker, replay.Segment.Length);\n', '')
engine = engine.replace('            DrainAndRelease(worker.Ingress.Reader, worker);\n', '')

# Remove replay read/write helper completely.
if '    private static async Task<PendingWriteResult> WriteReplayBlockAtOffsetAsync(' in engine:
    engine = cut_between(
        engine,
        '    private static async Task<PendingWriteResult> WriteReplayBlockAtOffsetAsync(',
        '    private static async Task<PendingWriteResult> WriteBlockAtOffsetAsync(',
        '',
        'remove replay writer')

# Release queue payload only needs shared data/control.
old = '''        switch (message)
        {
            case DataMessage data:
                ReleaseBranchBlock(worker, data.Block);
                break;
            case ReplayDataMessage replay:
                ReleaseBranchPayload(worker, replay.Segment.Length);
                break;
            default:
                ReleaseQueuedControl(message);
                break;
        }
'''
new = '''        if (message is DataMessage data)
            ReleaseBranchBlock(worker, data.Block);
        else
            ReleaseQueuedControl(message);
'''
engine = replace_once(engine, old, new, 'release simple queue payload')

# Single queue message definitions; remove SourceReadBlock and entire PipelineGovernor class.
start = engine.index('    private sealed class SourceReadBlock')
end = engine.index('    private abstract record FanoutMessage;', start)
engine = engine[:start] + engine[end:]
engine = engine.replace('    private sealed record DataMessage(SharedBlock Block, bool AllowIsolation = false) : FanoutMessage;\n',
                        '    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;\n')
engine = re.sub(r'    private sealed record ReplayDataMessage\([^\n]+\) : FanoutMessage;\n', '', engine)

start = engine.find('    internal sealed class PipelineGovernor')
if start >= 0:
    end = engine.find('    internal sealed class ResourceGovernor', start)
    if end < 0:
        raise RuntimeError('PipelineGovernor end missing')
    engine = engine[:start] + engine[end:]

# DestinationWorker: one queue, no Replay/Ingress/dispose.
start = engine.index('    private sealed class DestinationWorker')
end = engine.index('    private enum PendingWriteStatus', start)
worker_new = '''    private sealed class DestinationWorker
    {
        public Dictionary<string, VerificationPlan> VerificationPlans { get; } = new(StringComparer.Ordinal);
        private int _active = 1;
        private int _queueDepth;
        private long _pendingPayloadBytes;
        private long _peakPendingPayloadBytes;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(
            string root,
            int slot,
            DestinationProgress progress,
            StorageDeviceInfo device,
            DeviceScheduler deviceScheduler,
            AdaptiveControlByteBudget controlBudget)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            Device = device;
            DeviceScheduler = deviceScheduler;
            ControlBudget = controlBudget ?? throw new ArgumentNullException(nameof(controlBudget));
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        }

        public string Root { get; }
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public StorageDeviceInfo Device { get; }
        public DeviceScheduler DeviceScheduler { get; }
        internal AdaptiveControlByteBudget ControlBudget { get; }
        public Channel<FanoutMessage> Channel { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public long PendingPayloadBytes => Interlocked.Read(ref _pendingPayloadBytes);
        public long PeakPendingPayloadBytes => Interlocked.Read(ref _peakPendingPayloadBytes);
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

        public void ReservePendingPayload(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            var pending = Interlocked.Add(ref _pendingPayloadBytes, bytes);
            var peak = Interlocked.Read(ref _peakPendingPayloadBytes);
            while (pending > peak)
            {
                var observed = Interlocked.CompareExchange(ref _peakPendingPayloadBytes, pending, peak);
                if (observed == peak) break;
                peak = observed;
            }
        }

        public void ReleasePendingPayload(int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            while (true)
            {
                var current = Interlocked.Read(ref _pendingPayloadBytes);
                if (current < bytes)
                    throw new InvalidOperationException("La rama intentó liberar más payload FAN-OUT del que mantiene pendiente.");
                if (Interlocked.CompareExchange(ref _pendingPayloadBytes, current - bytes, current) == current)
                    return;
            }
        }

        public void IncrementQueueDepth()
        {
            var depth = Interlocked.Increment(ref _queueDepth);
            Progress.SetQueueDepth(depth);
        }

        public void DecrementQueueDepth()
        {
            var depth = Interlocked.Decrement(ref _queueDepth);
            if (depth < 0)
            {
                Interlocked.Exchange(ref _queueDepth, 0);
                throw new InvalidOperationException("La profundidad de cola del destino quedó negativa.");
            }
            Progress.SetQueueDepth(depth);
        }

        public void Fail(string error)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
        }
    }

'''
engine = engine[:start] + worker_new + engine[end:]

# Drain helper no longer needs reader identity.
engine = engine.replace('''        while (reader.TryRead(out var message))
        {
            if (ReferenceEquals(reader, worker.Ingress.Reader))
                worker.DecrementQueueDepth();
            ReleaseQueuedPayload(worker, message);
        }
''', '''        while (reader.TryRead(out var message))
        {
            worker.DecrementQueueDepth();
            ReleaseQueuedPayload(worker, message);
        }
''')

# Fixed aligned source block helper.
marker = '    private static int BufferAlignmentFor(StorageDeviceInfo device)\n'
idx = engine.index(marker)
helper = '''    private static int SelectSharedFanoutBlockSize(long fileSize, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        var desired = (int)Math.Min(SharedFanoutBlockBytes, Math.Max((long)alignment, fileSize));
        var remainder = desired % alignment;
        return remainder == 0 ? desired : checked(desired + alignment - remainder);
    }

'''
engine = engine[:idx] + helper + engine[idx:]

# Coordinated verification: all destination readers advance block-by-block together, one outstanding read per target.
start = engine.index('    private static async Task VerifyDestinationsAsync(')
end = engine.index('    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(', start)
verify_new = r'''    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        CopyJob job)
    {
        for (var slot = 0; slot < workers.Length; slot++)
        {
            if (!workers[slot].IsActive)
                continue;
            var entries = copy.Files
                .Where(entry => workers[slot].VerificationPlans.ContainsKey(PathKey(entry.RelativePath)))
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
                .Where(slot => workers[slot].IsActive && workers[slot].VerificationPlans.ContainsKey(PathKey(entry.RelativePath)))
                .ToArray();
            if (slots.Length == 0)
                continue;

            var plan = workers[slots[0]].VerificationPlans[PathKey(entry.RelativePath)];
            var targets = new List<CoordinatedVerifyTarget>(slots.Length);
            try
            {
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
                        slot, destination, copy.DestinationDevices[slot], workers[slot].DeviceScheduler, progress[slot]));
                }

                if (targets.Count == 0)
                    continue;

                foreach (var target in targets)
                    target.Open(plan.Blocks.Count == 0 ? 4096 : plan.Blocks.Max(block => block.Length));

                long offset = 0;
                foreach (var block in plan.Blocks)
                {
                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

                    var activeTargets = targets.Where(target => workers[target.Slot].IsActive).ToArray();
                    if (activeTargets.Length == 0)
                        break;

                    var reads = activeTargets
                        .Select(target => ReadVerifyTargetAsync(target, block.Length, offset, job))
                        .ToArray();
                    var results = await Task.WhenAll(reads).ConfigureAwait(false);
                    for (var index = 0; index < activeTargets.Length; index++)
                    {
                        var target = activeTargets[index];
                        var read = results[index];
                        if (read < block.Length)
                        {
                            workers[target.Slot].Fail($"Lectura incompleta durante verificación: {target.Path}");
                            continue;
                        }

                        var crcStarted = Stopwatch.GetTimestamp();
                        var actual = FastCrc32C.Compute(target.Buffer!.Memory.Span[..block.Length]);
                        job.Telemetry.RecordVerifyCrc32C(block.Length, Stopwatch.GetElapsedTime(crcStarted));
                        if (actual != block.Crc32C)
                        {
                            workers[target.Slot].Fail($"CRC32C no coincide durante verificación: {target.Path}");
                            continue;
                        }
                        target.Progress.AddVerified(block.Length);
                    }
                    offset = checked(offset + block.Length);
                }

                if (offset != plan.Length)
                {
                    foreach (var target in targets.Where(target => workers[target.Slot].IsActive))
                        workers[target.Slot].Fail($"Verificación incompleta: {target.Path}");
                }
                else
                {
                    foreach (var target in targets.Where(target => workers[target.Slot].IsActive))
                        target.Progress.MarkVerifyFileDone();
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
        var retries = 0;
        var lastCode = 0;
        while (true)
        {
            var requestBytes = target.Direct is null
                ? expectedBytes
                : AlignUp(expectedBytes, target.Direct.Alignment);
            using var io = await target.Scheduler.AcquireIoAsync(requestBytes, job.Token).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            try
            {
                int read;
                if (target.Direct is not null)
                {
                    try
                    {
                        read = await target.Direct.ReadAsync(target.Buffer!, requestBytes, offset, job.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                    {
                        target.SwitchToBuffered();
                        continue;
                    }
                }
                else
                {
                    read = await RandomAccess.ReadAsync(
                        target.BufferedHandle!, target.Buffer!.Memory[..expectedBytes], offset, job.Token).ConfigureAwait(false);
                }

                job.Telemetry.RecordVerifyRead(expectedBytes, Stopwatch.GetElapsedTime(started));
                if (retries > 0)
                    job.Telemetry.RecordIoRecovery("verify-read", target.Path,
                        target.Direct is null ? "buffered" : "direct", lastCode,
                        target.Scheduler.CurrentQueueDepth, retries, offset, recovered: true);
                return read;
            }
            catch (Exception ex) when (TransientIoErrorClassifier.IsTransient(ex))
            {
                lastCode = TransientIoErrorClassifier.GetNativeCodeOrZero(ex);
                retries++;
                target.Progress.AddRetry();
                job.Telemetry.RecordIoRecovery("verify-read", target.Path,
                    target.Direct is null ? "buffered" : "direct", lastCode,
                    target.Scheduler.CurrentQueueDepth, retries, offset, recovered: false);
                if (!target.Scheduler.RecordTransientFailure())
                    throw;
            }
        }
    }

    private static int AlignUp(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private sealed class CoordinatedVerifyTarget : IDisposable
    {
        internal CoordinatedVerifyTarget(
            int slot,
            string path,
            StorageDeviceInfo device,
            DeviceScheduler scheduler,
            DestinationProgress progress)
        {
            Slot = slot;
            Path = path;
            Device = device;
            Scheduler = scheduler;
            Progress = progress;
        }

        internal int Slot { get; }
        internal string Path { get; }
        internal StorageDeviceInfo Device { get; }
        internal DeviceScheduler Scheduler { get; }
        internal DestinationProgress Progress { get; }
        internal DirectIoSourceReader.OverlappedSession? Direct { get; private set; }
        internal Microsoft.Win32.SafeHandles.SafeFileHandle? BufferedHandle { get; private set; }
        internal SourceBufferLease? Buffer { get; private set; }

        internal void Open(int maximumBlockBytes)
        {
            var alignment = Math.Max(1, DirectIoSourceReader.RequiredAlignment(Device));
            var directCapacity = AlignUp(Math.Max(1, maximumBlockBytes), Math.Max(1, alignment));
            if (DirectIoSourceReader.TryOpenOverlappedForVerification(Path, Device, directCapacity, out var direct))
            {
                Direct = direct;
                Buffer = SourceBufferLease.RentAligned(directCapacity, Math.Max(Environment.SystemPageSize, alignment));
                return;
            }
            BufferedHandle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Buffer = SourceBufferLease.RentBuffered(Math.Max(1, maximumBlockBytes));
        }

        internal void SwitchToBuffered()
        {
            Direct?.Dispose();
            Direct = null;
            BufferedHandle ??= File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
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
engine = engine[:start] + verify_new + engine[end:]

# Remove replay/branch references, old source pipeline artifacts; assert after writes.
ENGINE.write_text(engine, encoding='utf-8')

# Models: replay is gone from public options.
models = MODELS.read_text(encoding='utf-8')
models = replace_once(models,
'''public sealed record CopyOptions(
    bool Verify = false,
    bool SkipSame = true,
    bool KeepGoing = false,
    bool EnableReplay = true);
''',
'''public sealed record CopyOptions(
    bool Verify = false,
    bool SkipSame = true,
    bool KeepGoing = false);
''',
'CopyOptions cleanup')
MODELS.write_text(models, encoding='utf-8')

# Telemetry: remove superseded pipeline/replay/branch/budget diagnostics. Add source read window for UI.
tel = TELEMETRY.read_text(encoding='utf-8')
tel = re.sub(r'public sealed record PipelineGovernorSnapshot\([\s\S]*?TimeSpan SourceReadTime\);\n\n', '', tel, count=1)
tel = tel.replace('    public PipelineGovernorSnapshot? PipelineGovernor { get; init; }\n', '')
tel = re.sub(r'    public long VerificationReadBudgetBytes \{ get; init; \}\n    public long PeakVerificationReadBytes \{ get; init; \}\n', '', tel)
tel = re.sub(r'    public long BranchReplayWriteBytes \{ get; init; \}\n    public TimeSpan BranchReplayWriteTime \{ get; init; \}\n    public long BranchReplayReadBytes \{ get; init; \}\n    public TimeSpan BranchReplayReadTime \{ get; init; \}\n    public long BranchReplaySegments \{ get; init; \}\n', '', tel)
tel = tel.replace('    public IReadOnlyList<BranchFlowSnapshot> BranchFlows { get; init; } = [];\n', '')
tel = tel.replace('    private readonly SlidingByteRateWindow _writeRate = new();\n', '    private readonly SlidingByteRateWindow _writeRate = new();\n    private readonly SlidingByteRateWindow _sourceReadRate = new();\n')
tel = re.sub(r'    private Func<PipelineGovernorSnapshot>\? _pipelineGovernorSnapshot;\n    private Func<IReadOnlyList<BranchFlowSnapshot>>\? _branchFlowSnapshot;\n', '', tel)
tel = re.sub(r'    private long _verificationReadBudgetBytes, _peakVerificationReadBytes;\n    private long _branchReplayWriteBytes, _branchReplayWriteTicks;\n    private long _branchReplayReadBytes, _branchReplayReadTicks, _branchReplaySegments;\n', '', tel)
tel = re.sub(r'\n    internal void AttachPipelineGovernor\([\s\S]*?\n    }\n\n    internal void AttachBranchFlows\([\s\S]*?\n    }\n', '\n', tel, count=1)
tel = tel.replace('    internal void RecordSourceRead(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceReadBytes, bytes); AddTicks(ref _sourceReadTicks, elapsed); }\n',
'''    internal void RecordSourceRead(int bytes, TimeSpan elapsed)
    {
        AddBytes(ref _sourceReadBytes, bytes);
        AddTicks(ref _sourceReadTicks, elapsed);
        if (bytes > 0) _sourceReadRate.Record(bytes);
    }
''')
tel = re.sub(r'    internal void RecordVerificationBufferBudget\([\s\S]*?\n    }\n', '', tel, count=1)
tel = re.sub(r'    internal void RecordBranchReplayWrite\([^\n]+\n    internal void RecordBranchReplayRead\([^\n]+\n', '', tel, count=1)
tel = tel.replace('        var pipeline = _pipelineGovernorSnapshot?.Invoke();\n\n', '')
tel = tel.replace('            PipelineGovernor = pipeline,\n', '')
tel = re.sub(r'            VerificationReadBudgetBytes = [^\n]+\n            PeakVerificationReadBytes = [^\n]+\n', '', tel)
tel = re.sub(r'            BranchReplayWriteBytes = [\s\S]*?            BranchReplaySegments = [^\n]+\n', '', tel, count=1)
tel = tel.replace('            BranchFlows = _branchFlowSnapshot?.Invoke() ?? [],\n', '')
# expose source recent rate
needle = '    public double SustainedWrite10sBytesPerSecond { get; init; }\n'
tel = tel.replace(needle, needle + '    public double SourceRead5sBytesPerSecond { get; init; }\n    public double SourceRead10sBytesPerSecond { get; init; }\n')
tel = tel.replace('        var sustained = _writeRate.Snapshot();\n', '        var sustained = _writeRate.Snapshot();\n        var sourceSustained = _sourceReadRate.Snapshot();\n')
tel = tel.replace('            SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,\n',
                  '            SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,\n            SourceRead5sBytesPerSecond = sourceSustained.FiveSecondsBytesPerSecond,\n            SourceRead10sBytesPerSecond = sourceSustained.TenSecondsBytesPerSecond,\n')
TELEMETRY.write_text(tel, encoding='utf-8')

# UI global speed = physical source-read rate, not aggregate logical writes.
main = MAIN.read_text(encoding='utf-8')
old = '''            var speed = paused
                ? 0d
                : snapshots.Aggregate<DestinationSnapshot, double>(0d, (sum, item) => sum + item.RecentBytesPerSecond);
'''
new = '''            var diagnostics = _job.DiagnosticsSnapshot();
            var speed = paused ? 0d : diagnostics.SourceRead5sBytesPerSecond;
'''
main = replace_once(main, old, new, 'UI physical source speed')
MAIN.write_text(main, encoding='utf-8')

# Retire obsolete files.
for rel in [
    'dotnet/RepartoCopier.Core/AdaptiveTransferSizer.cs',
    'dotnet/RepartoCopier.Core/BranchFlowSnapshot.cs',
    'dotnet/RepartoCopier.Core/BranchIsolationPolicy.cs',
    'dotnet/RepartoCopier.Core/BranchReplayPlacement.cs',
    'dotnet/RepartoCopier.Core/BranchReplayStore.cs',
    'dotnet/RepartoCopier.Core/FastVerificationReader.cs',
    'dotnet/RepartoCopier.Core/VerificationReadBudget.cs',
    'dotnet/RepartoCopier.Core.Tests/AdaptiveSourcePipelineTests.cs',
    'dotnet/RepartoCopier.Core.Tests/BranchReplayStoreTests.cs',
    'dotnet/RepartoCopier.Core.Tests/SourceWindowPerformanceTests.cs',
    'dotnet/RepartoCopier.Core.Tests/VerificationReadBudgetTests.cs',
]:
    p = ROOT / rel
    if p.exists():
        p.unlink()

# Replace branch-isolation architecture contract with shared-pool contract.
(TESTS / 'BranchPendingPayloadArchitectureTests.cs').write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class BranchPendingPayloadArchitectureTests
{
    [TestMethod]
    public void FanoutUsesOneSharedBlockAndOneQueuePerDestination()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("new SharedBlock(lease, read, readBufferSize, active.Count, bufferBudget)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("worker.Channel.Writer.TryWrite(message)", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("Ingress", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("StageBranchAsync", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("DetachBranchBlock", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SharedPoolBackpressureReplacesReplayAndPrivateBranchBuffers()
    {
        var root = FindRepositoryRoot();
        var core = Path.Combine(root, "dotnet", "RepartoCopier.Core");
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchReplayStore.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchReplayPlacement.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(core, "BranchIsolationPolicy.cs")));
        var engine = File.ReadAllText(Path.Combine(core, "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("SharedFanoutPoolBytes", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("bufferBudget.AcquireAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SourcePrefetchGovernorAndAdaptiveTransferSizerAreRetired()
    {
        var root = FindRepositoryRoot();
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "AdaptiveTransferSizer.cs")));
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsFalse(engine.Contains("PipelineGovernor", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("ReadAndFanOutPrefetchedAsync", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("SelectSharedFanoutBlockSize", StringComparison.Ordinal));
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

# Retry tests: retain real I/O recovery contracts, remove replay/isolation policy tests.
retry = TESTS / 'RetryAndReplayArchitectureTests.cs'
rt = retry.read_text(encoding='utf-8')
start = rt.find('    [TestMethod]\n    public void ReplayPlacementRequiresExactDifferentPhysicalDeviceFromEveryParticipant()')
if start >= 0:
    end = rt.find('    private static StorageDeviceInfo Device(', start)
    if end < 0: raise RuntimeError('retry replay test end missing')
    rt = rt[:start] + rt[end:]
    # Device helper is now unused; remove to class close.
    helper = rt.find('    private static StorageDeviceInfo Device(')
    if helper >= 0:
        rt = rt[:helper] + '}\n'
rt = rt.replace('public sealed class RetryAndReplayArchitectureTests', 'public sealed class IoRetryArchitectureTests')
retry_new = TESTS / 'IoRetryArchitectureTests.cs'
retry_new.write_text(rt, encoding='utf-8')
retry.unlink()

# New verification contract: coordinated streaming, one block at a time, no old per-target QD/read-budget engine.
(TESTS / 'VerificationArchitectureTests.cs').write_text(r'''using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class VerificationArchitectureTests
{
    [TestMethod]
    public void VerificationAdvancesAllDestinationsTogetherBySourceCrcBlock()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        Assert.IsTrue(engine.Contains("ReadVerifyTargetAsync", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("FastCrc32C.Compute(target.Buffer!.Memory.Span[..block.Length])", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "FastVerificationReader.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "dotnet", "RepartoCopier.Core", "VerificationReadBudget.cs")));
    }

    [TestMethod]
    public void VerificationDoesNotRereadSourceOrBuildPerDestinationReadPipelines()
    {
        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var start = engine.IndexOf("private static async Task VerifyDestinationsAsync", StringComparison.Ordinal);
        var end = engine.IndexOf("private static async Task<bool[][]> BuildVerifiedSkipMasksAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var verify = engine[start..end];
        Assert.IsFalse(verify.Contains("entry.SourcePath", StringComparison.Ordinal));
        Assert.IsFalse(verify.Contains("PendingRead", StringComparison.Ordinal));
        Assert.IsFalse(verify.Contains("ExplorationQueueDepth", StringComparison.Ordinal));
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

# Production verify test no longer expects a dynamic read budget.
p = TESTS / 'ProductionFastPathTests.cs'
pt = p.read_text(encoding='utf-8')
pt = re.sub(r'\n        Assert\.IsGreaterThan\(0L, metrics\.VerificationReadBudgetBytes\);[\s\S]*?para un archivo menor que el presupuesto\."\);', '', pt, count=1)
p.write_text(pt, encoding='utf-8')

# Remove diagnostics tests that existed only for removed replay/pipeline/read-budget counters where present.
for name in ['VerificationDiagnosticsTests.cs']:
    p = TESTS / name
    if p.exists():
        txt = p.read_text(encoding='utf-8')
        if 'VerificationReadBudgetBytes' in txt or 'PipelineGovernor' in txt or 'BranchReplay' in txt:
            p.unlink()

# Rewrite README performance architecture, and changelog current section succinctly.
readme = README.read_text(encoding='utf-8')
perf_start = readme.find('## Rendimiento')
perf_end = readme.find('## Compilar', perf_start)
if perf_start >= 0 and perf_end > perf_start:
    readme = readme[:perf_start] + '''## Rendimiento\n\nEl hot path FAN-OUT sigue un modelo shared-buffer deliberadamente simple: una lectura física del origen entra en un pool acotado de bloques alineados y el mismo bloque, con conteo de referencias, se entrega a una cola ligera por destino. Cada writer libera su referencia al completar la escritura; cuando el pool se llena, el lector espera espacio. No existen replay/spool, staging por rama ni copias privadas del payload en el camino normal.\n\nLa verificación opcional reutiliza los CRC32C calculados mientras el origen ya estaba en memoria. Después de copiar, todos los destinos avanzan coordinadamente bloque a bloque: una lectura outstanding por destino, CRC32C inmediato, comparación y reutilización del buffer. El origen no se vuelve a leer durante Verify.\n\nLa telemetría mantiene lectura física del origen, escrituras, recuperación de I/O, Direct I/O y tiempos de copy/verify para que el cuello de botella sea medible.\n\n''' + readme[perf_end:]
README.write_text(readme, encoding='utf-8')

ch = CHANGELOG.read_text(encoding='utf-8')
if '## Unreleased — main' in ch:
    s = ch.index('## Unreleased — main')
    e = ch.find('\n## v2.1.0', s)
    if e > s:
        ch = ch[:s] + '''## Unreleased — main\n\n- Hot path FAN-OUT reconstruido sobre un pool compartido con refcount: una lectura del origen, el mismo bloque para todos los destinos y una única cola ligera por writer.\n- Eliminados ReplayStore, placement de replay, aislamiento por copia privada, staging por rama, PipelineGovernor y AdaptiveTransferSizer del camino productivo.\n- Backpressure simplificado: cuando el pool compartido se llena, el lector espera a que los writers liberen referencias; no se escribe I/O temporal adicional.\n- Verificación rediseñada como lectura coordinada de todos los destinos contra los CRC32C obtenidos durante la copia; no vuelve a leer el origen y no crea pipelines QD independientes por destino.\n- Velocidad global de la UI representa ahora la tasa física de lectura del origen en ventana de 5 s, no la suma lógica de escrituras de destinos.\n- Se conservan Direct I/O seguro, recuperación transitoria, atomic commit, recovery, SkipSame, cancelación y telemetría.\n- Contratos y pruebas de arquitectura antiguos eliminados junto con sus implementaciones sin consumidor.\n\n''' + ch[e+1:]
CHANGELOG.write_text(ch, encoding='utf-8')

# Hard architectural guards before compile.
all_cs = '\n'.join(p.read_text(encoding='utf-8', errors='ignore') for p in (ROOT / 'dotnet').rglob('*.cs'))
for forbidden in ['BranchReplayStore', 'BranchReplayPlacement', 'BranchIsolationPolicy', 'StageBranchAsync', 'DetachBranchBlock', 'ReplayDataMessage', 'FastVerificationReader', 'VerificationReadBudget', 'PipelineGovernor', 'AdaptiveTransferSizer', 'EnableReplay']:
    if forbidden in all_cs:
        raise RuntimeError(f'legacy architecture token remains: {forbidden}')

print('ExtremeCopy-baseline shared FAN-OUT migration applied')
