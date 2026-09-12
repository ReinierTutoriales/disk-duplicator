from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

s = s.replace('    private const int SourcePrefetchDepth = 2;\n', '    private const int SourcePrefetchPhysicalCapacity = 4;\n', 1)

# Add one pipeline governor per copy operation.
s = s.replace('        using var resources = new ResourceGovernor();\n', '        using var resources = new ResourceGovernor();\n        var pipeline = new PipelineGovernor();\n', 1)

s = s.replace(
    'await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job).ConfigureAwait(false);',
    'await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job, pipeline).ConfigureAwait(false);',
    1)

s = s.replace('        ConcurrentDictionary<string, byte[]> expectedHashes,\n        CopyJob job)\n',
              '        ConcurrentDictionary<string, byte[]> expectedHashes,\n        CopyJob job,\n        PipelineGovernor pipeline)\n', 1)

s = s.replace(
    '? await ReadAndFanOutPrefetchedAsync(entry, active, bufferBudget, job).ConfigureAwait(false)\n                    : await ReadAndFanOutSequentialAsync(entry, active, bufferBudget, job).ConfigureAwait(false);',
    '? await ReadAndFanOutPrefetchedAsync(entry, active, bufferBudget, job, pipeline).ConfigureAwait(false)\n                    : await ReadAndFanOutSequentialAsync(entry, active, bufferBudget, job, pipeline).ConfigureAwait(false);',
    1)

# Sequential: measure buffer wait, read latency, delivery backpressure.
s = s.replace('        AdaptiveByteBudget bufferBudget,\n        CopyJob job)\n    {\n        using var hasher',
              '        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline)\n    {\n        using var hasher', 1)
s = s.replace('            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);\n',
              '            var budgetStarted = Stopwatch.GetTimestamp();\n            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);\n            pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));\n', 1)
s = s.replace('                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);\n',
              '                var readStarted = Stopwatch.GetTimestamp();\n                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);\n                pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));\n', 1)
s = s.replace('            await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);\n',
              '            var deliveryStarted = Stopwatch.GetTimestamp();\n            await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);\n            pipeline.RecordDeliveryWait(Stopwatch.GetElapsedTime(deliveryStarted));\n', 1)

# Prefetched signature + physical capacity.
s = s.replace('        AdaptiveByteBudget bufferBudget,\n        CopyJob job)\n    {\n        using var prefetchCancel',
              '        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline)\n    {\n        using var prefetchCancel', 1)
s = s.replace('new BoundedChannelOptions(SourcePrefetchDepth)', 'new BoundedChannelOptions(SourcePrefetchPhysicalCapacity)', 1)
s = s.replace('            bufferBudget,\n            job,\n            prefetchCancel.Token);',
              '            bufferBudget,\n            job,\n            pipeline,\n            prefetchCancel.Token);', 1)

# Replace await foreach with manual read loop to measure consumer starvation and release adaptive slot on dequeue.
old = '''            await foreach (var sourceBlock in sourceQueue.Reader.ReadAllAsync(job.Token).ConfigureAwait(false))
            {
                var recipients = active.Where(worker => worker.IsActive).ToArray();
'''
new = '''            while (true)
            {
                var consumerStarted = Stopwatch.GetTimestamp();
                if (!await sourceQueue.Reader.WaitToReadAsync(job.Token).ConfigureAwait(false))
                    break;
                pipeline.RecordConsumerWait(Stopwatch.GetElapsedTime(consumerStarted));
                if (!sourceQueue.Reader.TryRead(out var sourceBlock))
                    continue;
                pipeline.ReleasePrefetchSlot();
                var recipients = active.Where(worker => worker.IsActive).ToArray();
'''
if old not in s:
    raise SystemExit('prefetch consumer anchor not found')
s = s.replace(old, new, 1)
s = s.replace('                await DeliverAsync(recipients, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);\n',
              '                var deliveryStarted = Stopwatch.GetTimestamp();\n                await DeliverAsync(recipients, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);\n                pipeline.RecordDeliveryWait(Stopwatch.GetElapsedTime(deliveryStarted));\n', 1)
s = s.replace('            while (sourceQueue.Reader.TryRead(out var leftover))\n                leftover.Release();\n',
              '            while (sourceQueue.Reader.TryRead(out var leftover))\n            {\n                pipeline.ReleasePrefetchSlot();\n                leftover.Release();\n            }\n', 1)

# Prefetch producer: adaptive logical slot + measurements.
s = s.replace('        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        CancellationToken token)\n',
              '        AdaptiveByteBudget bufferBudget,\n        CopyJob job,\n        PipelineGovernor pipeline,\n        CancellationToken token)\n', 1)
s = s.replace('                await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);\n',
              '                await pipeline.AcquirePrefetchSlotAsync(token).ConfigureAwait(false);\n                var budgetStarted = Stopwatch.GetTimestamp();\n                try\n                {\n                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);\n                    pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));\n                }\n                catch\n                {\n                    pipeline.ReleasePrefetchSlot();\n                    throw;\n                }\n', 1)
s = s.replace('                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);\n',
              '                    var readStarted = Stopwatch.GetTimestamp();\n                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);\n                    pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));\n', 1)
# On read failure/EOF, release prefetch slot too because no block will be dequeued.
s = s.replace('                    bufferBudget.Release(readBufferSize);\n                    throw;\n',
              '                    bufferBudget.Release(readBufferSize);\n                    pipeline.ReleasePrefetchSlot();\n                    throw;\n', 1)
s = s.replace('                    bufferBudget.Release(readBufferSize);\n                    break;\n',
              '                    bufferBudget.Release(readBufferSize);\n                    pipeline.ReleasePrefetchSlot();\n                    break;\n', 1)
# If channel write fails, block release returns memory but slot also must return.
s = s.replace('                    block.Release();\n                    throw;\n',
              '                    block.Release();\n                    pipeline.ReleasePrefetchSlot();\n                    throw;\n', 1)

# Insert adaptive pipeline governor before ResourceGovernor.
anchor = '    private sealed class ResourceGovernor : IDisposable\n'
idx = s.find(anchor)
if idx < 0:
    raise SystemExit('ResourceGovernor anchor not found')

pipeline_class = r'''    private sealed class PipelineGovernor
    {
        private const int MinPrefetch = 1;
        private const int InitialPrefetch = 2;
        private const int MaxPrefetch = SourcePrefetchPhysicalCapacity;
        private const int SamplesPerDecision = 8;

        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource> _slotWaiters = new();
        private int _prefetchLimit = InitialPrefetch;
        private int _inFlight;
        private int _samples;
        private double _consumerWaitMs;
        private double _deliveryWaitMs;
        private double _budgetWaitMs;
        private double _readMs;

        public ValueTask AcquirePrefetchSlotAsync(CancellationToken token)
        {
            lock (_gate)
            {
                if (_inFlight < _prefetchLimit && _slotWaiters.Count == 0)
                {
                    _inFlight++;
                    return ValueTask.CompletedTask;
                }
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _slotWaiters.Enqueue(waiter);
                return new ValueTask(waiter.Task.WaitAsync(token));
            }
        }

        public void ReleasePrefetchSlot()
        {
            TaskCompletionSource? ready = null;
            lock (_gate)
            {
                if (_inFlight > 0)
                    _inFlight--;
                EvaluateLocked();
                if (_inFlight < _prefetchLimit && _slotWaiters.Count > 0)
                {
                    ready = _slotWaiters.Dequeue();
                    _inFlight++;
                }
            }
            ready?.TrySetResult();
        }

        public void RecordConsumerWait(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Consumer);
        public void RecordDeliveryWait(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Delivery);
        public void RecordBudgetWait(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Budget);
        public void RecordSourceRead(TimeSpan elapsed) => RecordSample(elapsed.TotalMilliseconds, SampleKind.Read);

        private void RecordSample(double milliseconds, SampleKind kind)
        {
            lock (_gate)
            {
                switch (kind)
                {
                    case SampleKind.Consumer: _consumerWaitMs += milliseconds; break;
                    case SampleKind.Delivery: _deliveryWaitMs += milliseconds; break;
                    case SampleKind.Budget: _budgetWaitMs += milliseconds; break;
                    case SampleKind.Read: _readMs += milliseconds; break;
                }
                _samples++;
                EvaluateLocked();
            }
        }

        private void EvaluateLocked()
        {
            if (_samples < SamplesPerDecision)
                return;

            var starvation = _consumerWaitMs;
            var pressure = _deliveryWaitMs + _budgetWaitMs;
            var sourceCost = _readMs;

            if (pressure > starvation * 1.5 && pressure > sourceCost)
                _prefetchLimit = Math.Max(MinPrefetch, _prefetchLimit - 1);
            else if (starvation > pressure * 1.5 && starvation > sourceCost * 0.25)
                _prefetchLimit = Math.Min(MaxPrefetch, _prefetchLimit + 1);

            _samples = 0;
            _consumerWaitMs = 0;
            _deliveryWaitMs = 0;
            _budgetWaitMs = 0;
            _readMs = 0;

            while (_inFlight < _prefetchLimit && _slotWaiters.Count > 0)
            {
                var waiter = _slotWaiters.Dequeue();
                _inFlight++;
                waiter.TrySetResult();
            }
        }

        private enum SampleKind
        {
            Consumer,
            Delivery,
            Budget,
            Read,
        }
    }

'''
s = s[:idx] + pipeline_class + s[idx:]

engine.write_text(s, encoding='utf-8')

# Regression: large-file pipeline under several consumers preserves exact bytes while exercising adaptive prefetch.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]\n    public async Task FeedbackGovernedVerificationPreservesIntegrity()\n'''
if anchor not in t:
    raise SystemExit('test anchor not found')
new_test = '''    [TestMethod]\n    public async Task AdaptivePipelinePrefetchPreservesExactLargeFileFanOut()\n    {\n        using var temp = new TempDirectory("adaptive-pipeline-prefetch");\n        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;\n        var payload = new byte[48 * 1024 * 1024 + 731];\n        new Random(86420).NextBytes(payload);\n        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), payload);\n\n        var destinations = Enumerable.Range(0, 4)\n            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)\n            .ToArray();\n        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);\n        await using var job = CopyEngine.Start(plan);\n        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));\n        AssertHealthy(job);\n\n        foreach (var destination in destinations)\n            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "large.bin")));\n    }\n\n'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')
