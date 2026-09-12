from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "dotnet/RepartoCopier.Core/CopyEngine.cs"
TELEMETRY = ROOT / "dotnet/RepartoCopier.Core/CopyTelemetry.cs"
TESTS = ROOT / "dotnet/RepartoCopier.Core.Tests/FanoutBackpressureTests.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one regex match, found {count}")
    return updated


engine = ENGINE.read_text(encoding="utf-8")

engine = replace_once(
    engine,
    """    private const int ChannelCapacity = 16;\n    private const int AdaptiveInitialQueue = 4;\n    private const int AdaptiveMinQueue = 2;\n    private const int AdaptiveMaxQueue = 8;\n    private const int FastSamplesToGrow = 8;\n    private const int Retries = 2;\n    private static readonly TimeSpan FastBlockWrite = TimeSpan.FromMilliseconds(40);\n    private static readonly TimeSpan SlowBlockWrite = TimeSpan.FromMilliseconds(250);\n""",
    """    // 4 GiB / 16 MiB = 256 maximum live data blocks. At the public\n    // 256-destination ceiling that is 65,536 channel references. The same\n    // global budget also bounds Begin/End-heavy trees whose payload-byte\n    // budget would otherwise see almost no pressure.\n    private const int ControlBacklogCapacity = 64 * 1024;\n    private const int Retries = 2;\n""",
    "constants",
)

engine = replace_once(
    engine,
    """        var pipeline = new PipelineGovernor();\n        try\n""",
    """        var pipeline = new PipelineGovernor();\n        var controlBudget = new GlobalControlBacklogBudget(ControlBacklogCapacity);\n        try\n""",
    "control budget construction",
)

engine = replace_once(
    engine,
    """            var queueDepth = ChannelCapacity;\n            workers = copy.DestinationRoots\n                .Select((root, index) => new DestinationWorker(root, index, progress[index], queueDepth))\n                .ToArray();\n\n            var writerTasks = workers\n""",
    """            workers = copy.DestinationRoots\n                .Select((root, index) => new DestinationWorker(root, index, progress[index], controlBudget))\n                .ToArray();\n\n            var copyPhaseStarted = Stopwatch.GetTimestamp();\n            var writerTasks = workers\n""",
    "worker construction",
)

engine = replace_once(
    engine,
    """            if (producerError is not null)\n                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();\n            if (writerError is not null)\n                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();\n\n            if (options.Verify && !token.IsCancellationRequested)\n                await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job, resources).ConfigureAwait(false);\n""",
    """            job.Telemetry.RecordCopyPhase(Stopwatch.GetElapsedTime(copyPhaseStarted));\n\n            if (producerError is not null)\n                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();\n            if (writerError is not null)\n                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();\n\n            if (options.Verify && !token.IsCancellationRequested)\n            {\n                var verifyPhaseStarted = Stopwatch.GetTimestamp();\n                try\n                {\n                    await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job, resources).ConfigureAwait(false);\n                }\n                finally\n                {\n                    job.Telemetry.RecordVerifyPhase(Stopwatch.GetElapsedTime(verifyPhaseStarted));\n                }\n            }\n""",
    "phase telemetry",
)

new_delivery = r'''    private static async Task DeliverAsync(
        IReadOnlyList<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        var index = 0;
        try
        {
            for (; index < recipients.Count; index++)
                await DeliverOneAsync(recipients[index], message, countsData, job).ConfigureAwait(false);
        }
        catch
        {
            // DeliverOneAsync owns and releases the current recipient's data reference
            // when it throws. References for recipients not visited yet still belong to
            // this dispatcher and must be released explicitly.
            if (message is DataMessage data)
            {
                for (var remaining = index + 1; remaining < recipients.Count; remaining++)
                    data.Block.Release();
            }
            throw;
        }
    }

    private static async ValueTask DeliverOneAsync(
        DestinationWorker worker,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        if (!worker.IsActive)
        {
            ReleaseIfData(message);
            return;
        }

        var controlOwned = false;
        var queueOwned = false;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            var controlWaitStarted = Stopwatch.GetTimestamp();
            await worker.ControlBudget.AcquireAsync(job.Token).ConfigureAwait(false);
            var controlWait = Stopwatch.GetElapsedTime(controlWaitStarted);
            job.Telemetry.RecordControlBacklogWait(controlWait);
            job.Telemetry.ObserveControlBacklog(worker.ControlBudget.Used);
            controlOwned = true;

            // Fail() can race the initial IsActive read while we wait for a global
            // control slot. Do not enqueue into a worker that died in that window.
            if (!worker.IsActive)
            {
                worker.ControlBudget.Release();
                controlOwned = false;
                ReleaseIfData(message);
                return;
            }

            worker.IncrementQueueDepth();
            queueOwned = true;

            // Unbounded channels have no per-worker capacity gate. false therefore
            // means the writer side was completed between IsActive and TryWrite.
            if (worker.Channel.Writer.TryWrite(message))
            {
                controlOwned = false; // ownership transfers to the queued message
                queueOwned = false;
                return;
            }

            worker.DecrementQueueDepth();
            queueOwned = false;
            worker.ControlBudget.Release();
            controlOwned = false;
            ReleaseIfData(message);
            if (worker.IsActive)
                worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        catch (OperationCanceledException)
        {
            if (queueOwned) worker.DecrementQueueDepth();
            if (controlOwned) worker.ControlBudget.Release();
            ReleaseIfData(message);
            throw;
        }
        catch
        {
            if (queueOwned) worker.DecrementQueueDepth();
            if (controlOwned) worker.ControlBudget.Release();
            ReleaseIfData(message);
            throw;
        }
    }

'''
engine = regex_once(
    engine,
    r"    private static async Task DeliverAsync\(.*?(?=    private static async Task WriterLoopAsync\()",
    new_delivery,
    "delivery methods",
)

new_writer = r'''    private static async Task WriterLoopAsync(
        DestinationWorker worker,
        CopyOptions options,
        CopyJob job,
        ConcurrentDictionary<string, byte[]> expectedHashes)
    {
        CurrentFile? current = null;
        using var recovery = new RecoveryCheckpointWriter(worker.Root);
        try
        {
            worker.Progress.SetPhase(DestinationPhase.Copying);
            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                // QueueDepth and the global control budget represent messages waiting
                // in channels only. Once dequeued, release both before doing physical I/O.
                worker.DecrementQueueDepth();
                worker.ControlBudget.Release();
                var data = message as DataMessage;
                try
                {
                    if (!worker.IsActive)
                        continue;

                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                    switch (message)
                    {
                        case BeginMessage begin:
                            current = BeginFile(worker, begin.Entry);
                            break;
                        case DataMessage chunkData when current is not null:
                            try
                            {
                                if (!current.Failed)
                                {
                                    var started = Stopwatch.GetTimestamp();
                                    await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);
                                    var elapsed = Stopwatch.GetElapsedTime(started);
                                    job.Telemetry.RecordWrite(chunkData.Block.Length, elapsed);
                                    current.Copied += chunkData.Block.Length;
                                    worker.Progress.AddWritten(chunkData.Block.Length);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                current.Failed = true;
                                current.Stream?.Dispose();
                                current.Stream = null;
                                TryDelete(current.PartPath);
                                if (current.Copied > 0) worker.Progress.RollbackWritten((ulong)current.Copied);
                                if (options.KeepGoing) worker.Progress.MarkError(ex.Message);
                                else worker.Fail(ex.Message);
                            }
                            break;
                        case DataMessage:
                            // A data message without an active file is discarded safely;
                            // the SharedBlock reference is released by the iteration finally.
                            break;
                        case EndMessage end when current is not null:
                            FinishFile(worker, current, end.Hash, options, recovery, job);
                            if (!current.Failed)
                                expectedHashes[PathKey(current.Entry.RelativePath)] = end.Hash;
                            current = null;
                            break;
                    }
                    worker.NoteProgress();
                }
                finally
                {
                    // The dequeued data reference belongs to this iteration regardless
                    // of failure, cancellation, pause cancellation, or orphaned state.
                    data?.Block.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            worker.Progress.SetPhase(DestinationPhase.Cancelled, "Cancelado");
        }
        catch (Exception ex)
        {
            worker.Fail(ex.Message);
        }
        finally
        {
            if (current is not null)
            {
                current.Stream?.Dispose();
                TryDelete(current.PartPath);
                if (current.Copied > 0 && !current.Failed)
                    worker.Progress.RollbackWritten((ulong)current.Copied);
            }
            DrainAndRelease(worker);
        }
    }

'''
engine = regex_once(
    engine,
    r"    private static async Task WriterLoopAsync\(.*?(?=    private static CurrentFile BeginFile\()",
    new_writer,
    "writer loop",
)

engine = replace_once(
    engine,
    """    private static void DrainAndRelease(DestinationWorker worker)\n    {\n        while (worker.Channel.Reader.TryRead(out var message))\n        {\n            if (message is DataMessage data)\n            {\n                worker.DecrementQueueDepth();\n                data.Block.Release();\n            }\n        }\n    }\n""",
    """    private static void DrainAndRelease(DestinationWorker worker)\n    {\n        while (worker.Channel.Reader.TryRead(out var message))\n        {\n            worker.DecrementQueueDepth();\n            worker.ControlBudget.Release();\n            ReleaseIfData(message);\n        }\n    }\n""",
    "drain ownership",
)

new_worker = r'''    private sealed class DestinationWorker
    {
        private int _active = 1;
        private int _queueDepth;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;

        public DestinationWorker(
            string root,
            int slot,
            DestinationProgress progress,
            GlobalControlBacklogBudget controlBudget)
        {
            Root = root;
            Slot = slot;
            Progress = progress;
            ControlBudget = controlBudget;
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
        public GlobalControlBacklogBudget ControlBudget { get; }
        public Channel<FanoutMessage> Channel { get; }
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);

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
engine = regex_once(
    engine,
    r"    private sealed class DestinationWorker\n    \{.*?(?=    private sealed class CurrentFile\()",
    new_worker,
    "destination worker",
)

old_hash = """                var hashStarted = Stopwatch.GetTimestamp();\n                if (resources is null)\n                {\n                    hasher.UpdateWithJoin(buffer.AsSpan(0, read));\n                }\n                else\n                {\n                    using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);\n                    hasher.UpdateWithJoin(buffer.AsSpan(0, read));\n                }\n                if (verification)\n                    telemetry?.RecordVerifyHash(read, Stopwatch.GetElapsedTime(hashStarted));\n"""
new_hash = """                if (resources is null)\n                {\n                    var hashStarted = Stopwatch.GetTimestamp();\n                    hasher.UpdateWithJoin(buffer.AsSpan(0, read));\n                    if (verification)\n                        telemetry?.RecordVerifyHash(read, Stopwatch.GetElapsedTime(hashStarted));\n                }\n                else\n                {\n                    var cpuWaitStarted = Stopwatch.GetTimestamp();\n                    using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);\n                    if (verification)\n                        telemetry?.RecordVerifyCpuWait(Stopwatch.GetElapsedTime(cpuWaitStarted));\n                    var hashStarted = Stopwatch.GetTimestamp();\n                    hasher.UpdateWithJoin(buffer.AsSpan(0, read));\n                    if (verification)\n                        telemetry?.RecordVerifyHash(read, Stopwatch.GetElapsedTime(hashStarted));\n                }\n"""
engine = replace_once(engine, old_hash, new_hash, "verify CPU wait split")

ENGINE.write_text(engine, encoding="utf-8", newline="\n")

TELEMETRY.write_text('''using System.Diagnostics;\nusing System.Threading;\n\nnamespace RepartoCopier.Core;\n\npublic sealed record CopyDiagnosticsSnapshot(\n    long SourceReadBytes,\n    TimeSpan SourceReadTime,\n    long SourceHashBytes,\n    TimeSpan SourceHashTime,\n    TimeSpan BufferWaitTime,\n    TimeSpan FanoutWaitTime,\n    TimeSpan QueueWaitTime,\n    TimeSpan ControlBacklogWaitTime,\n    long WrittenBytes,\n    TimeSpan WriteTime,\n    int DurableFlushes,\n    TimeSpan DurableFlushTime,\n    int Commits,\n    TimeSpan CommitTime,\n    int RecoveryEvents,\n    TimeSpan RecoveryTime,\n    long VerifyReadBytes,\n    TimeSpan VerifyReadTime,\n    long VerifyHashBytes,\n    TimeSpan VerifyHashTime,\n    TimeSpan VerifyCpuWaitTime,\n    int PeakControlBacklogMessages,\n    long PeakBufferedBytes,\n    long MaximumObservedBufferTargetBytes,\n    TimeSpan CopyPhaseElapsed,\n    TimeSpan VerifyPhaseElapsed,\n    TimeSpan Elapsed)\n{\n    public double SourceReadBytesPerSecond => Rate(SourceReadBytes, SourceReadTime);\n    public double SourceHashBytesPerSecond => Rate(SourceHashBytes, SourceHashTime);\n    public double WriteBytesPerSecond => Rate(WrittenBytes, WriteTime);\n    public double VerifyReadBytesPerSecond => Rate(VerifyReadBytes, VerifyReadTime);\n    public double VerifyHashBytesPerSecond => Rate(VerifyHashBytes, VerifyHashTime);\n\n    private static double Rate(long bytes, TimeSpan elapsed) =>\n        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes / elapsed.TotalSeconds;\n}\n\ninternal sealed class CopyTelemetry\n{\n    private readonly long _started = Stopwatch.GetTimestamp();\n    private long _sourceReadBytes, _sourceReadTicks;\n    private long _sourceHashBytes, _sourceHashTicks;\n    private long _bufferWaitTicks, _fanoutWaitTicks, _queueWaitTicks, _controlBacklogWaitTicks;\n    private long _writtenBytes, _writeTicks;\n    private int _flushes, _commits, _recoveryEvents;\n    private long _flushTicks, _commitTicks, _recoveryTicks;\n    private long _verifyReadBytes, _verifyReadTicks;\n    private long _verifyHashBytes, _verifyHashTicks, _verifyCpuWaitTicks;\n    private int _peakControlBacklogMessages;\n    private long _peakBufferedBytes, _maxObservedBufferTargetBytes;\n    private long _copyPhaseTicks, _verifyPhaseTicks;\n\n    internal void RecordSourceRead(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceReadBytes, bytes); AddTicks(ref _sourceReadTicks, elapsed); }\n    internal void RecordSourceHash(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceHashBytes, bytes); AddTicks(ref _sourceHashTicks, elapsed); }\n    internal void RecordBufferWait(TimeSpan elapsed) => AddTicks(ref _bufferWaitTicks, elapsed);\n    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);\n    internal void RecordQueueWait(TimeSpan elapsed) => AddTicks(ref _queueWaitTicks, elapsed);\n    internal void RecordControlBacklogWait(TimeSpan elapsed) => AddTicks(ref _controlBacklogWaitTicks, elapsed);\n    internal void RecordWrite(int bytes, TimeSpan elapsed) { AddBytes(ref _writtenBytes, bytes); AddTicks(ref _writeTicks, elapsed); }\n    internal void RecordFlush(TimeSpan elapsed) { Interlocked.Increment(ref _flushes); AddTicks(ref _flushTicks, elapsed); }\n    internal void RecordCommit(TimeSpan elapsed) { Interlocked.Increment(ref _commits); AddTicks(ref _commitTicks, elapsed); }\n    internal void RecordRecovery(TimeSpan elapsed) { Interlocked.Increment(ref _recoveryEvents); AddTicks(ref _recoveryTicks, elapsed); }\n    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }\n    internal void RecordVerifyHash(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyHashBytes, bytes); AddTicks(ref _verifyHashTicks, elapsed); }\n    internal void RecordVerifyCpuWait(TimeSpan elapsed) => AddTicks(ref _verifyCpuWaitTicks, elapsed);\n    internal void RecordCopyPhase(TimeSpan elapsed) => AddTicks(ref _copyPhaseTicks, elapsed);\n    internal void RecordVerifyPhase(TimeSpan elapsed) => AddTicks(ref _verifyPhaseTicks, elapsed);\n\n    internal void ObserveControlBacklog(int usedMessages) =>\n        UpdateMax(ref _peakControlBacklogMessages, usedMessages);\n\n    internal void ObserveBuffer(long usedBytes, long targetBytes)\n    {\n        UpdateMax(ref _peakBufferedBytes, usedBytes);\n        UpdateMax(ref _maxObservedBufferTargetBytes, targetBytes);\n    }\n\n    internal CopyDiagnosticsSnapshot Snapshot() => new(\n        Interlocked.Read(ref _sourceReadBytes), ToTimeSpan(Interlocked.Read(ref _sourceReadTicks)),\n        Interlocked.Read(ref _sourceHashBytes), ToTimeSpan(Interlocked.Read(ref _sourceHashTicks)),\n        ToTimeSpan(Interlocked.Read(ref _bufferWaitTicks)),\n        ToTimeSpan(Interlocked.Read(ref _fanoutWaitTicks)),\n        ToTimeSpan(Interlocked.Read(ref _queueWaitTicks)),\n        ToTimeSpan(Interlocked.Read(ref _controlBacklogWaitTicks)),\n        Interlocked.Read(ref _writtenBytes), ToTimeSpan(Interlocked.Read(ref _writeTicks)),\n        Volatile.Read(ref _flushes), ToTimeSpan(Interlocked.Read(ref _flushTicks)),\n        Volatile.Read(ref _commits), ToTimeSpan(Interlocked.Read(ref _commitTicks)),\n        Volatile.Read(ref _recoveryEvents), ToTimeSpan(Interlocked.Read(ref _recoveryTicks)),\n        Interlocked.Read(ref _verifyReadBytes), ToTimeSpan(Interlocked.Read(ref _verifyReadTicks)),\n        Interlocked.Read(ref _verifyHashBytes), ToTimeSpan(Interlocked.Read(ref _verifyHashTicks)),\n        ToTimeSpan(Interlocked.Read(ref _verifyCpuWaitTicks)),\n        Volatile.Read(ref _peakControlBacklogMessages),\n        Interlocked.Read(ref _peakBufferedBytes), Interlocked.Read(ref _maxObservedBufferTargetBytes),\n        ToTimeSpan(Interlocked.Read(ref _copyPhaseTicks)),\n        ToTimeSpan(Interlocked.Read(ref _verifyPhaseTicks)),\n        Stopwatch.GetElapsedTime(_started));\n\n    private static void AddBytes(ref long target, int bytes) { if (bytes > 0) Interlocked.Add(ref target, bytes); }\n    private static void AddTicks(ref long target, TimeSpan elapsed)\n    {\n        if (elapsed <= TimeSpan.Zero) return;\n        var ticks = (long)(elapsed.TotalSeconds * Stopwatch.Frequency);\n        if (ticks > 0) Interlocked.Add(ref target, ticks);\n    }\n    private static TimeSpan ToTimeSpan(long ticks) => ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);\n    private static void UpdateMax(ref long target, long value)\n    {\n        var current = Volatile.Read(ref target);\n        while (value > current)\n        {\n            var observed = Interlocked.CompareExchange(ref target, value, current);\n            if (observed == current) return;\n            current = observed;\n        }\n    }\n    private static void UpdateMax(ref int target, int value)\n    {\n        var current = Volatile.Read(ref target);\n        while (value > current)\n        {\n            var observed = Interlocked.CompareExchange(ref target, value, current);\n            if (observed == current) return;\n            current = observed;\n        }\n    }\n}\n''', encoding="utf-8", newline="\n")

TESTS.write_text('''using Microsoft.VisualStudio.TestTools.UnitTesting;\nusing RepartoCopier.Core;\n\nnamespace RepartoCopier.Core.Tests;\n\n[TestClass]\npublic sealed class FanoutBackpressureTests\n{\n    [TestMethod]\n    public async Task EmptyAndTinyFilesUseBoundedControlPlaneWithoutLosingOrder()\n    {\n        using var temp = new TempDirectory("control-plane");\n        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;\n        const int files = 256;\n        for (var index = 0; index < files; index++)\n        {\n            var path = Path.Combine(source, $"f-{index:D4}.bin");\n            if ((index & 1) == 0)\n                File.WriteAllBytes(path, []);\n            else\n                File.WriteAllBytes(path, [(byte)(index & 0xff)]);\n        }\n\n        var destinations = Enumerable.Range(0, 3)\n            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)\n            .ToArray();\n        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);\n        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false, SkipSame: false, KeepGoing: false));\n        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));\n\n        AssertHealthy(job);\n        Assert.IsTrue(job.DiagnosticsSnapshot().PeakControlBacklogMessages > 0);\n        Assert.IsTrue(job.DiagnosticsSnapshot().CopyPhaseElapsed > TimeSpan.Zero);\n        foreach (var destination in destinations)\n        {\n            var root = Path.Combine(destination, "Origen");\n            Assert.AreEqual(files, Directory.EnumerateFiles(root).Count());\n            for (var index = 0; index < files; index++)\n            {\n                var bytes = await File.ReadAllBytesAsync(Path.Combine(root, $"f-{index:D4}.bin"));\n                if ((index & 1) == 0)\n                    Assert.AreEqual(0, bytes.Length);\n                else\n                    CollectionAssert.AreEqual(new byte[] { (byte)(index & 0xff) }, bytes);\n            }\n        }\n    }\n\n    [TestMethod]\n    public async Task VerificationTelemetrySeparatesGovernorWaitFromHashCompute()\n    {\n        using var temp = new TempDirectory("verify-telemetry");\n        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;\n        var payload = new byte[8 * 1024 * 1024 + 113];\n        new Random(424242).NextBytes(payload);\n        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);\n        var destinations = Enumerable.Range(0, 3)\n            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)\n            .ToArray();\n\n        await using var job = CopyEngine.Start(\n            CopyPlan.Create(source, destinations, false, false),\n            new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));\n        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));\n        AssertHealthy(job);\n\n        var metrics = job.DiagnosticsSnapshot();\n        Assert.IsTrue(metrics.VerifyReadBytes >= (long)payload.Length * destinations.Length);\n        Assert.IsTrue(metrics.VerifyHashBytes >= (long)payload.Length * destinations.Length);\n        Assert.IsTrue(metrics.VerifyHashTime > TimeSpan.Zero);\n        Assert.IsTrue(metrics.VerifyCpuWaitTime >= TimeSpan.Zero);\n        Assert.IsTrue(metrics.VerifyPhaseElapsed > TimeSpan.Zero);\n    }\n\n    private static void AssertHealthy(CopyJob job)\n    {\n        var bad = job.Snapshot().Where(item => item.Phase != DestinationPhase.Done).ToArray();\n        if (bad.Length == 0) return;\n        Assert.Fail(string.Join(" | ", bad.Select(item => $"{item.Label}: {item.Phase}: {item.Error}")));\n    }\n\n    private sealed class TempDirectory : IDisposable\n    {\n        public TempDirectory(string name)\n        {\n            Path = System.IO.Path.Combine(\n                System.IO.Path.GetTempPath(),\n                $"repartocopier-fanout-{name}-{Guid.NewGuid():N}");\n            Directory.CreateDirectory(Path);\n        }\n\n        public string Path { get; }\n\n        public void Dispose()\n        {\n            try { Directory.Delete(Path, recursive: true); }\n            catch { }\n        }\n    }\n}\n''', encoding="utf-8", newline="\n")

print("fan-out refactor staged")
