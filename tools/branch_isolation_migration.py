from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / 'dotnet/RepartoCopier.Core/CopyEngine.cs'
SIZER = ROOT / 'dotnet/RepartoCopier.Core/AdaptiveTransferSizer.cs'
TELEMETRY = ROOT / 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
TEST = ROOT / 'dotnet/RepartoCopier.Core.Tests/BranchPendingPayloadArchitectureTests.cs'
GATE = ROOT / 'dotnet/RepartoCopier.Core/BranchReplayGate.cs'


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected exactly one match, found {count}')
    return text.replace(old, new, 1)

engine = ENGINE.read_text(encoding='utf-8')

# 1) Run two independent per-destination stages: producer -> branch staging -> writer.
old = '''            var copyPhaseStarted = Stopwatch.GetTimestamp();
            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job))
                .ToArray();
'''
new = '''            job.Telemetry.AttachBranchFlows(() => workers.Select(worker => worker.FlowSnapshot()).ToArray());

            var copyPhaseStarted = Stopwatch.GetTimestamp();
            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job))
                .ToArray();
            var stagingTasks = workers
                .Select(worker => StageBranchAsync(worker, job))
                .ToArray();
'''
engine = replace_once(engine, old, new, 'attach branch staging')

old = '''            finally
            {
                foreach (var worker in workers)
                    worker.Channel.Writer.TryComplete(producerError);
            }

            Exception? writerError = null;
            try
            {
                await Task.WhenAll(writerTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                writerError = ex;
            }
'''
new = '''            finally
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
            catch (Exception ex)
            {
                writerError = ex;
            }
'''
engine = replace_once(engine, old, new, 'complete ingress before writers')

old = '''            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
                worker.Dispose();
            }
'''
new = '''            foreach (var worker in workers)
            {
                worker.Ingress.Writer.TryComplete();
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker.Ingress.Reader, worker);
                DrainAndRelease(worker.Channel.Reader, worker);
                worker.Dispose();
            }
'''
engine = replace_once(engine, old, new, 'final dual drain')

# 2) Replace producer-side replay I/O with enqueue-only delivery.
start = engine.index('    private static async Task DeliverDataAsync(')
end = engine.index('    private static async ValueTask DeliverControlAsync(', start)
new_method = '''    private static async Task DeliverDataAsync(
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

                var backlogOwned = false;
                var pendingPayloadOwned = false;
                var queueOwned = false;
                var blockOwned = true;
                try
                {
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    backlogOwned = true;
                    worker.ReservePendingPayload(message.Block.Length);
                    pendingPayloadOwned = true;

                    if (!worker.IsActive)
                    {
                        ReleaseBranchPayload(worker, message.Block.Length);
                        backlogOwned = false;
                        pendingPayloadOwned = false;
                        message.Block.Release();
                        blockOwned = false;
                        continue;
                    }

                    var branchMessage = new DataMessage(message.Block, recipients.Count > 1);
                    worker.IncrementQueueDepth();
                    queueOwned = true;
                    if (worker.Ingress.Writer.TryWrite(branchMessage))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        pendingPayloadOwned = false;
                        blockOwned = false;
                        continue;
                    }

                    worker.DecrementQueueDepth();
                    queueOwned = false;
                    ReleaseBranchPayload(worker, message.Block.Length);
                    backlogOwned = false;
                    pendingPayloadOwned = false;
                    message.Block.Release();
                    blockOwned = false;
                    if (worker.IsActive)
                        worker.Fail("El canal de entrada del destino se cerró antes de recibir todos los datos.");
                }
                catch
                {
                    if (queueOwned)
                        worker.DecrementQueueDepth();
                    if (backlogOwned || pendingPayloadOwned)
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

    private static async Task StageBranchAsync(DestinationWorker worker, CopyJob job)
    {
        Exception? completionError = null;
        try
        {
            await foreach (var message in worker.Ingress.Reader.ReadAllAsync(job.Token).ConfigureAwait(false))
            {
                worker.DecrementQueueDepth();
                FanoutMessage staged = message;
                var ownsOriginal = message is DataMessage;
                try
                {
                    if (!worker.IsActive)
                    {
                        ReleaseQueuedPayload(worker, message);
                        ownsOriginal = false;
                        continue;
                    }

                    if (message is DataMessage data &&
                        data.AllowIsolation &&
                        BranchIsolationPolicy.ShouldDetach(
                            worker.PendingPayloadBytes,
                            data.Block.Length,
                            worker.DeviceScheduler.CurrentQueueDepth,
                            worker.DeviceScheduler.BacklogTargetBytes))
                    {
                        if (worker.ReplayStore.IsEnabled)
                        {
                            try
                            {
                                var replayStarted = Stopwatch.GetTimestamp();
                                var segment = await worker.ReplayStore.SpillAsync(
                                    data.Block.Memory,
                                    data.Block.VerificationCrc32C,
                                    job.Token).ConfigureAwait(false);
                                job.Telemetry.RecordBranchReplayWrite(
                                    segment.Length,
                                    Stopwatch.GetElapsedTime(replayStarted));
                                data.Block.Release();
                                ownsOriginal = false;
                                staged = new ReplayDataMessage(segment);
                            }
                            catch (IOException)
                            {
                                staged = new DataMessage(DetachBranchBlock(worker, data.Block), false);
                                ownsOriginal = false;
                            }
                        }
                        else
                        {
                            staged = new DataMessage(DetachBranchBlock(worker, data.Block), false);
                            ownsOriginal = false;
                        }
                    }

                    if (!worker.Channel.Writer.TryWrite(staged))
                    {
                        ReleaseQueuedPayload(worker, staged);
                        ownsOriginal = false;
                        if (worker.IsActive)
                            worker.Fail("El canal de escritura del destino se cerró antes de recibir todos los datos.");
                    }
                    else
                    {
                        ownsOriginal = false;
                    }
                }
                catch
                {
                    if (ownsOriginal)
                        ReleaseQueuedPayload(worker, message);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            completionError = ex;
            worker.Fail(ex.Message);
            throw;
        }
        finally
        {
            worker.Channel.Writer.TryComplete(completionError);
        }
    }

    private static SharedBlock DetachBranchBlock(DestinationWorker worker, SharedBlock source)
    {
        SourceBufferLease? detached = SourceBufferLease.RentAligned(
            source.Length,
            BufferAlignmentFor(worker.Device));
        try
        {
            source.Memory.CopyTo(detached.Memory[..source.Length]);
            var result = new SharedBlock(detached, source.Length, source.VerificationCrc32C);
            detached = null;
            source.Release();
            return result;
        }
        finally
        {
            detached?.Dispose();
        }
    }

'''
engine = engine[:start] + new_method + engine[end:]

# Controls go through ingress too so ordering remains exact.
engine = replace_once(engine,
    '            if (worker.Channel.Writer.TryWrite(delivery))',
    '            if (worker.Ingress.Writer.TryWrite(delivery))',
    'control ingress')
engine = engine.replace('El canal del destino se cerró antes de recibir todos los datos.',
                        'El canal de entrada del destino se cerró antes de recibir todos los datos.', 1)

# 3) Writer no longer owns producer queue depth/backlog dequeue accounting.
engine = replace_once(engine,
'''            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                worker.DecrementQueueDepth();
                var controlDelivery = message as ControlDelivery;
''',
'''            await foreach (var message in worker.Channel.Reader.ReadAllAsync())
            {
                var controlDelivery = message as ControlDelivery;
''',
'writer dequeue accounting')
engine = replace_once(engine,
'''                var dataOwnedByWriter = data is not null;
                var replayOwnedByWriter = replay is not null;
                if (data is not null)
                    worker.DeviceScheduler.ReleaseBacklog(data.Block.Length);
                else if (replay is not null)
                    worker.DeviceScheduler.ReleaseBacklog(replay.Segment.Length);
                try
''',
'''                var dataOwnedByWriter = data is not null;
                var replayOwnedByWriter = replay is not null;
                try
''',
'keep physical backlog until completion')
engine = replace_once(engine,
'                        worker.ReleasePendingPayload(replay.Segment.Length);',
'                        ReleaseBranchPayload(worker, replay.Segment.Length);',
'replay writer release')
engine = replace_once(engine,
'            worker.ReleasePendingPayload(segment.Length);\n            return PendingWriteResult.Failed(ex);',
'            ReleaseBranchPayload(worker, segment.Length);\n            return PendingWriteResult.Failed(ex);',
'replay failure release')

# Dynamic per-branch write admission: no unbounded pending task list.
old = '''                            var offset = current.ReserveWriteOffset(chunkData.Block.Length);
                            current.VerificationBlocks.Add(
'''
new = '''                            var admissionError = await EnsureWriteWindowAsync(worker, current, job).ConfigureAwait(false);
                            if (admissionError is not null)
                            {
                                FailCurrentFile(worker, current, options, admissionError.Message);
                                break;
                            }

                            var offset = current.ReserveWriteOffset(chunkData.Block.Length);
                            current.VerificationBlocks.Add(
'''
engine = replace_once(engine, old, new, 'data write admission')
old = '''                            var replayOffset = current.ReserveWriteOffset(replayData.Segment.Length);
                            current.VerificationBlocks.Add(
'''
new = '''                            var replayAdmissionError = await EnsureWriteWindowAsync(worker, current, job).ConfigureAwait(false);
                            if (replayAdmissionError is not null)
                            {
                                FailCurrentFile(worker, current, options, replayAdmissionError.Message);
                                break;
                            }

                            var replayOffset = current.ReserveWriteOffset(replayData.Segment.Length);
                            current.VerificationBlocks.Add(
'''
engine = replace_once(engine, old, new, 'replay write admission')

anchor = '    private static void PruneCompletedSuccesses(CurrentFile current)\n'
idx = engine.index(anchor)
helper = '''    private static async Task<Exception?> EnsureWriteWindowAsync(
        DestinationWorker worker,
        CurrentFile current,
        CopyJob job)
    {
        PruneCompletedSuccesses(current);
        var admissionWindow = Math.Max(1, worker.DeviceScheduler.ExplorationQueueDepth);
        if (current.PendingWrites.Count < admissionWindow)
            return null;
        return await DrainPendingWritesAsync(worker, current, job).ConfigureAwait(false);
    }

'''
engine = engine[:idx] + helper + engine[idx:]

# 4) Backlog/payload lifetime ends at actual branch completion, not dequeue.
old = '''    private static void ReleaseBranchBlock(DestinationWorker worker, SharedBlock block)
    {
        worker.ReleasePendingPayload(block.Length);
        block.Release();
    }
'''
new = '''    private static void ReleaseBranchBlock(DestinationWorker worker, SharedBlock block)
    {
        ReleaseBranchPayload(worker, block.Length);
        block.Release();
    }

    private static void ReleaseBranchPayload(DestinationWorker worker, int bytes)
    {
        worker.ReleasePendingPayload(bytes);
        worker.DeviceScheduler.ReleaseBacklog(bytes);
    }

    private static void ReleaseQueuedPayload(DestinationWorker worker, FanoutMessage message)
    {
        switch (message)
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
    }
'''
engine = replace_once(engine, old, new, 'branch release semantics')

# Generalized drain for ingress and writer queues.
start = engine.index('    private static void DrainAndRelease(DestinationWorker worker)')
end = engine.index('    private static void ReleaseQueuedControl(', start)
new_drain = '''    private static void DrainAndRelease(ChannelReader<FanoutMessage> reader, DestinationWorker worker)
    {
        while (reader.TryRead(out var message))
        {
            if (ReferenceEquals(reader, worker.Ingress.Reader))
                worker.DecrementQueueDepth();
            ReleaseQueuedPayload(worker, message);
        }
    }

'''
engine = engine[:start] + new_drain + engine[end:]
engine = engine.replace('            DrainAndRelease(worker);',
                        '            DrainAndRelease(worker.Ingress.Reader, worker);\n            DrainAndRelease(worker.Channel.Reader, worker);')

# 5) Message and worker topology.
engine = replace_once(engine,
'    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;',
'    private sealed record DataMessage(SharedBlock Block, bool AllowIsolation = false) : FanoutMessage;',
'data message isolation flag')

old = '''            ReplayStore = new BranchReplayStore(replayDirectory);
            ReplayGate = new BranchReplayGate();
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
'''
new = '''            ReplayStore = new BranchReplayStore(replayDirectory);
            Ingress = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
            Channel = System.Threading.Channels.Channel.CreateUnbounded<FanoutMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
'''
engine = replace_once(engine, old, new, 'worker channels')
engine = replace_once(engine,
'''        internal BranchReplayStore ReplayStore { get; }
        internal BranchReplayGate ReplayGate { get; }
        public Channel<FanoutMessage> Channel { get; }
''',
'''        internal BranchReplayStore ReplayStore { get; }
        public Channel<FanoutMessage> Ingress { get; }
        public Channel<FanoutMessage> Channel { get; }
''',
'remove replay gate add ingress')

old = '''        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
'''
new = '''        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

        internal BranchFlowSnapshot FlowSnapshot() => new(
            Root,
            Volatile.Read(ref _queueDepth),
            PendingPayloadBytes,
            PeakPendingPayloadBytes,
            DeviceScheduler.QueuedBytes,
            DeviceScheduler.OutstandingIo,
            DeviceScheduler.CurrentQueueDepth,
            ReplayStore.IsEnabled);

        public void NoteProgress() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
'''
engine = replace_once(engine, old, new, 'branch flow snapshot')

old = '''            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Channel.Writer.TryComplete();
'''
new = '''            Progress.MarkError(error);
            Progress.SetPhase(DestinationPhase.Failed, error);
            Ingress.Writer.TryComplete();
            Channel.Writer.TryComplete();
'''
engine = replace_once(engine, old, new, 'worker fail channels')

ENGINE.write_text(engine, encoding='utf-8')

# Branch isolation threshold is tied to the current physical queue window, never a time delay or arbitrary global cap.
policy = ROOT / 'dotnet/RepartoCopier.Core/BranchIsolationPolicy.cs'
policy.write_text('''namespace RepartoCopier.Core;\n\ninternal static class BranchIsolationPolicy\n{\n    internal static long SharedRetentionTargetBytes(\n        int blockBytes,\n        int currentQueueDepth,\n        long deviceBacklogTargetBytes)\n    {\n        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockBytes);\n        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentQueueDepth);\n        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceBacklogTargetBytes);\n\n        var qdWindow = currentQueueDepth > long.MaxValue / blockBytes\n            ? long.MaxValue\n            : (long)blockBytes * currentQueueDepth;\n        return Math.Max(blockBytes, Math.Min(deviceBacklogTargetBytes, qdWindow));\n    }\n\n    internal static bool ShouldDetach(\n        long pendingPayloadBytes,\n        int blockBytes,\n        int currentQueueDepth,\n        long deviceBacklogTargetBytes)\n    {\n        if (pendingPayloadBytes <= 0)\n            return false;\n        return pendingPayloadBytes > SharedRetentionTargetBytes(\n            blockBytes, currentQueueDepth, deviceBacklogTargetBytes);\n    }\n}\n''', encoding='utf-8')

flow = ROOT / 'dotnet/RepartoCopier.Core/BranchFlowSnapshot.cs'
flow.write_text('''namespace RepartoCopier.Core;\n\npublic sealed record BranchFlowSnapshot(\n    string Destination,\n    int IngressQueueDepth,\n    long PendingPayloadBytes,\n    long PeakPendingPayloadBytes,\n    long PhysicalBacklogBytes,\n    int OutstandingIo,\n    int CurrentQueueDepth,\n    bool ReplayAvailable);\n''', encoding='utf-8')

# 6) Source block sizing must not be reduced because one branch explores a high QD.
sizer = SIZER.read_text(encoding='utf-8')
old = '''        var maximumQd = devices.Count == 0
            ? 1
            : devices.Max(item => Math.Max(1, item.CurrentQueueDepth));
        var residentBlocks = checked(
            Math.Max(currentPrefetchLimit, maximumQd) + Math.Max(0, activeDestinations - 1));
'''
new = '''        // A source block exists once regardless of destination count. Branch-local
        // staging detaches lagging consumers, so one destination's QD must not shrink
        // the global source transfer size for every other destination.
        var residentBlocks = checked(Math.Max(1, currentPrefetchLimit) + 1);
'''
sizer = replace_once(sizer, old, new, 'remove max-QD source coupling')
old = '''        double measuredBytesPerOperation = 0;
        var measuredCount = 0;
        foreach (var device in devices)
        {
            if (device.BestThroughputBytesPerSecond <= 0 ||
                device.BestAverageLatencyMilliseconds <= 0)
            {
                continue;
            }

            var qd = Math.Max(1, device.BestQueueDepth);
            var bytes = device.BestThroughputBytesPerSecond *
                (device.BestAverageLatencyMilliseconds / 1000.0) / qd;
            if (!double.IsFinite(bytes) || bytes <= 0)
                continue;
            measuredBytesPerOperation += bytes;
            measuredCount++;
        }

        long candidate = memoryBound;
        if (measuredCount > 0)
        {
            var measured = (long)Math.Max(
                requiredAlignment,
                measuredBytesPerOperation / measuredCount);
            candidate = Math.Min(memoryBound, measured);
        }
'''
new = '''        double largestMeasuredBytesPerOperation = 0;
        foreach (var device in devices)
        {
            if (device.BestThroughputBytesPerSecond <= 0 ||
                device.BestAverageLatencyMilliseconds <= 0)
            {
                continue;
            }

            var qd = Math.Max(1, device.BestQueueDepth);
            var bytes = device.BestThroughputBytesPerSecond *
                (device.BestAverageLatencyMilliseconds / 1000.0) / qd;
            if (!double.IsFinite(bytes) || bytes <= 0)
                continue;
            largestMeasuredBytesPerOperation = Math.Max(largestMeasuredBytesPerOperation, bytes);
        }

        long candidate = memoryBound;
        if (largestMeasuredBytesPerOperation > 0)
        {
            var measured = (long)Math.Max(requiredAlignment, largestMeasuredBytesPerOperation);
            candidate = Math.Min(memoryBound, measured);
        }
'''
sizer = replace_once(sizer, old, new, 'fast-branch transfer signal')
SIZER.write_text(sizer, encoding='utf-8')

# 7) Expose real per-branch flow diagnostics.
tel = TELEMETRY.read_text(encoding='utf-8')
tel = replace_once(tel,
'    public IReadOnlyList<IoRecoveryEvent> RecentIoRecoveryEvents { get; init; } = [];\n',
'    public IReadOnlyList<IoRecoveryEvent> RecentIoRecoveryEvents { get; init; } = [];\n    public IReadOnlyList<BranchFlowSnapshot> BranchFlows { get; init; } = [];\n',
'branch snapshot property')
tel = replace_once(tel,
'    private Func<PipelineGovernorSnapshot>? _pipelineGovernorSnapshot;\n',
'    private Func<PipelineGovernorSnapshot>? _pipelineGovernorSnapshot;\n    private Func<IReadOnlyList<BranchFlowSnapshot>>? _branchFlowSnapshot;\n',
'branch snapshot provider field')
tel = replace_once(tel,
'''    internal void AttachPipelineGovernor(Func<PipelineGovernorSnapshot> snapshotProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        _pipelineGovernorSnapshot = snapshotProvider;
    }
''',
'''    internal void AttachPipelineGovernor(Func<PipelineGovernorSnapshot> snapshotProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        _pipelineGovernorSnapshot = snapshotProvider;
    }

    internal void AttachBranchFlows(Func<IReadOnlyList<BranchFlowSnapshot>> snapshotProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        _branchFlowSnapshot = snapshotProvider;
    }
''',
'attach branch flows')
tel = replace_once(tel,
'            RecentIoRecoveryEvents = _ioRecoveryEvents.ToArray(),\n',
'            RecentIoRecoveryEvents = _ioRecoveryEvents.ToArray(),\n            BranchFlows = _branchFlowSnapshot?.Invoke() ?? [],\n',
'branch snapshots in output')
TELEMETRY.write_text(tel, encoding='utf-8')

# 8) Replace the old coupling contract with branch-isolation contracts.
TEST.write_text('''using Microsoft.VisualStudio.TestTools.UnitTesting;\nusing RepartoCopier.Core;\n\nnamespace RepartoCopier.Core.Tests;\n\n[TestClass]\npublic sealed class BranchPendingPayloadArchitectureTests\n{\n    [TestMethod]\n    public void IsolationThresholdTracksPhysicalQueueWindow()\n    {\n        Assert.AreEqual(32L * 1024 * 1024, BranchIsolationPolicy.SharedRetentionTargetBytes(4 * 1024 * 1024, 8, 256L * 1024 * 1024));\n        Assert.AreEqual(256L * 1024 * 1024, BranchIsolationPolicy.SharedRetentionTargetBytes(8 * 1024 * 1024, 64, 256L * 1024 * 1024));\n        Assert.IsFalse(BranchIsolationPolicy.ShouldDetach(32L * 1024 * 1024, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));\n        Assert.IsTrue(BranchIsolationPolicy.ShouldDetach(36L * 1024 * 1024, 4 * 1024 * 1024, 8, 256L * 1024 * 1024));\n    }\n\n    [TestMethod]\n    public void ProducerHotPathDoesNotAwaitReplayIo()\n    {\n        var root = FindRepositoryRoot();\n        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        var deliverStart = engine.IndexOf("private static async Task DeliverDataAsync", StringComparison.Ordinal);\n        var stageStart = engine.IndexOf("private static async Task StageBranchAsync", StringComparison.Ordinal);\n        Assert.IsTrue(deliverStart >= 0 && stageStart > deliverStart);\n        var producerDelivery = engine[deliverStart..stageStart];\n        Assert.IsFalse(producerDelivery.Contains("SpillAsync", StringComparison.Ordinal));\n        Assert.IsTrue(engine[stageStart..].Contains("ReplayStore.SpillAsync", StringComparison.Ordinal));\n    }\n\n    [TestMethod]\n    public void PhysicalBacklogIsReleasedWithCompletedBranchPayload()\n    {\n        var root = FindRepositoryRoot();\n        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        var releaseStart = engine.IndexOf("private static void ReleaseBranchPayload", StringComparison.Ordinal);\n        Assert.IsTrue(releaseStart >= 0);\n        var tail = engine[releaseStart..Math.Min(engine.Length, releaseStart + 500)];\n        Assert.IsTrue(tail.Contains("ReleasePendingPayload", StringComparison.Ordinal));\n        Assert.IsTrue(tail.Contains("DeviceScheduler.ReleaseBacklog", StringComparison.Ordinal));\n    }\n\n    [TestMethod]\n    public void PendingWritesHaveAdaptiveAdmissionWindow()\n    {\n        var root = FindRepositoryRoot();\n        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        Assert.IsTrue(engine.Contains("EnsureWriteWindowAsync", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("DeviceScheduler.ExplorationQueueDepth", StringComparison.Ordinal));\n    }\n\n    private static string FindRepositoryRoot()\n    {\n        var current = new DirectoryInfo(AppContext.BaseDirectory);\n        while (current is not null)\n        {\n            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln")))\n                return current.FullName;\n            current = current.Parent;\n        }\n        throw new AssertFailedException("No se encontró la raíz del repositorio.");\n    }\n}\n''', encoding='utf-8')

# Retire the superseded 500ms replay gate completely.
if GATE.exists():
    GATE.unlink()

# Architecture guard: no old gate or inline replay remains.
if 'ReplayGate' in ENGINE.read_text(encoding='utf-8'):
    raise RuntimeError('legacy ReplayGate consumer remains')
if 'await worker.ReplayStore.SpillAsync' not in ENGINE.read_text(encoding='utf-8'):
    raise RuntimeError('async replay staging was not installed')

print('branch isolation migration applied')
