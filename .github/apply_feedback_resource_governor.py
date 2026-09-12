from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

s = s.replace('    private const int AbsoluteMaxVerificationParallelism = 8;\n', '')

old = '''        DestinationWorker[] workers = [];
        try
        {
            var skipMasks = options.SkipSame
                ? await BuildVerifiedSkipMasksAsync(copy, progress, job, token).ConfigureAwait(false)
                : CreateEmptySkipMasks(copy.Files.Count, copy.DestinationRoots.Length);
'''
new = '''        DestinationWorker[] workers = [];
        using var resources = new ResourceGovernor();
        try
        {
            var skipMasks = options.SkipSame
                ? await BuildVerifiedSkipMasksAsync(copy, progress, job, token, resources).ConfigureAwait(false)
                : CreateEmptySkipMasks(copy.Files.Count, copy.DestinationRoots.Length);
'''
if old not in s:
    raise SystemExit('run start anchor not found')
s = s.replace(old, new, 1)

old = '''            using var copyResources = ResourceGovernor.EnterCopy(workers.Length);
            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job, expectedHashes))
                .ToArray();
'''
new = '''            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job, expectedHashes))
                .ToArray();
'''
if old not in s:
    raise SystemExit('copy lease anchor not found')
s = s.replace(old, new, 1)

s = s.replace('                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job).ConfigureAwait(false);',
              '                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job).ConfigureAwait(false);', 1)

old = '''            copyResources.Dispose();
            if (options.Verify && !token.IsCancellationRequested)
                await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job).ConfigureAwait(false);
'''
new = '''            if (options.Verify && !token.IsCancellationRequested)
                await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job, resources).ConfigureAwait(false);
'''
if old not in s:
    raise SystemExit('verify call anchor not found')
s = s.replace(old, new, 1)

# Rewrite verification to use a dynamically sized async gate based on measured process CPU.
start = s.index('    private static async Task VerifyDestinationsAsync(')
end = s.index('    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(', start)
verify = '''    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        ConcurrentDictionary<string, byte[]> expectedHashes,
        CopyJob job,
        ResourceGovernor resources)
    {
        var activeSlots = Enumerable.Range(0, workers.Length)
            .Where(slot => workers[slot].IsActive)
            .ToArray();
        var tasks = activeSlots.Select(async slot =>
        {
            using var lease = await resources.EnterCpuWorkAsync(job.Token).ConfigureAwait(false);
            progress[slot].SetPhase(DestinationPhase.Verifying);
            foreach (var entry in copy.Files)
            {
                job.Token.ThrowIfCancellationRequested();
                job.WaitIfPaused(job.Token);
                if (!expectedHashes.TryGetValue(PathKey(entry.RelativePath), out var expected))
                    continue;
                var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                if (!File.Exists(destination))
                {
                    workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                    break;
                }
                var actual = await HashFileAsync(destination, job.Token).ConfigureAwait(false);
                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    workers[slot].Fail($"BLAKE3 no coincide: {destination}");
                    break;
                }
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

'''
s = s[:start] + verify + s[end:]

# Rewrite skip-same candidate hashing to use the same feedback gate.
start = s.index('    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(')
end = s.index('    private static bool[][] CreateEmptySkipMasks', start)
old_block = s[start:end]
old_sig = '''    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(
        PreparedCopy copy,
        DestinationProgress[] progress,
        CopyJob job,
        CancellationToken token)
'''
new_sig = '''    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(
        PreparedCopy copy,
        DestinationProgress[] progress,
        CopyJob job,
        CancellationToken token,
        ResourceGovernor resources)
'''
if old_sig not in old_block:
    raise SystemExit('skip signature anchor not found')
new_block = old_block.replace(old_sig, new_sig, 1)
old_parallel = '''            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = ResourceGovernor.VerificationParallelism(candidates.Count),
                    CancellationToken = token,
                },
                async (slot, cancellationToken) =>
                {
                    var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                    var destinationHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
                    if (destinationHash.AsSpan().SequenceEqual(sourceHash))
                    {
                        masks[fileIndex][slot] = true;
                        progress[slot].SetLastFile(entry.RelativePath);
                    }
                }).ConfigureAwait(false);
'''
new_parallel = '''            var checks = candidates.Select(async slot =>
            {
                using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
                var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                var destinationHash = await HashFileAsync(destination, token).ConfigureAwait(false);
                if (destinationHash.AsSpan().SequenceEqual(sourceHash))
                {
                    masks[fileIndex][slot] = true;
                    progress[slot].SetLastFile(entry.RelativePath);
                }
            }).ToArray();
            await Task.WhenAll(checks).ConfigureAwait(false);
'''
if old_parallel not in new_block:
    raise SystemExit('skip parallel anchor not found')
new_block = new_block.replace(old_parallel, new_parallel, 1)
s = s[:start] + new_block + s[end:]

# Replace previous destination-count/process-priority governor with CPU-feedback async concurrency gate.
start = s.index('    private static class ResourceGovernor')
end = s.index('    private sealed class AdaptiveByteBudget', start)
governor = '''    private sealed class ResourceGovernor : IDisposable
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
        private const double CpuGrowThreshold = 0.60;
        private const double CpuShrinkThreshold = 0.85;

        private readonly object _gate = new();
        private readonly Queue<CpuWaiter> _waiters = new();
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
        private TimeSpan _lastCpu;
        private long _lastSampleTimestamp;
        private int _limit = 1;
        private int _active;
        private bool _disposed;

        public ResourceGovernor()
        {
            _lastCpu = _process.TotalProcessorTime;
            _lastSampleTimestamp = Stopwatch.GetTimestamp();
        }

        public ValueTask<CpuLease> EnterCpuWorkAsync(CancellationToken token)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                SampleAndAdjustLocked();
                if (_active < _limit)
                {
                    _active++;
                    return ValueTask.FromResult(new CpuLease(this));
                }

                var waiter = new CpuWaiter();
                _waiters.Enqueue(waiter);
                return new ValueTask<CpuLease>(WaitForLeaseAsync(waiter, token));
            }
        }

        private async Task<CpuLease> WaitForLeaseAsync(CpuWaiter waiter, CancellationToken token)
        {
            try
            {
                await waiter.Ready.Task.WaitAsync(token).ConfigureAwait(false);
                return new CpuLease(this);
            }
            catch
            {
                lock (_gate)
                {
                    waiter.Cancelled = true;
                    PumpLocked();
                }
                throw;
            }
        }

        private void ReleaseCpuWork()
        {
            lock (_gate)
            {
                if (_active > 0)
                    _active--;
                SampleAndAdjustLocked();
                PumpLocked();
            }
        }

        private void SampleAndAdjustLocked()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now);
            if (elapsed < SampleInterval)
                return;

            TimeSpan cpu;
            try
            {
                cpu = _process.TotalProcessorTime;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var cpuSeconds = Math.Max(0, (cpu - _lastCpu).TotalSeconds);
            var wallSeconds = Math.Max(0.001, elapsed.TotalSeconds);
            var utilization = Math.Clamp(cpuSeconds / (wallSeconds * _processorCount), 0.0, 1.0);
            _lastCpu = cpu;
            _lastSampleTimestamp = now;

            if (utilization >= CpuShrinkThreshold)
                _limit = Math.Max(1, _limit - 1);
            else if (utilization <= CpuGrowThreshold && _waiters.Count > 0)
                _limit = Math.Min(_processorCount, _limit + 1);
        }

        private void PumpLocked()
        {
            while (_active < _limit && _waiters.Count > 0)
            {
                var waiter = _waiters.Dequeue();
                if (waiter.Cancelled)
                    continue;
                _active++;
                waiter.Ready.TrySetResult();
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                while (_waiters.Count > 0)
                {
                    var waiter = _waiters.Dequeue();
                    waiter.Ready.TrySetException(new ObjectDisposedException(nameof(ResourceGovernor)));
                }
            }
            _process.Dispose();
        }

        private sealed class CpuWaiter
        {
            public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
        }

        internal sealed class CpuLease : IDisposable
        {
            private ResourceGovernor? _owner;

            public CpuLease(ResourceGovernor owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseCpuWork();
            }
        }
    }

'''
s = s[:start] + governor + s[end:]

engine.write_text(s, encoding='utf-8')

# Add a regression that exercises adaptive verification without assuming a destination-count profile.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task FanOutTenDestinationsRemainExactWithoutWorkerStarvation()
'''
if anchor not in t:
    raise SystemExit('test anchor not found')
new_test = '''    [TestMethod]
    public async Task FeedbackGovernedVerificationPreservesIntegrity()
    {
        using var temp = new TempDirectory("feedback-governor-integrity");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        for (var index = 0; index < 6; index++)
        {
            var payload = new byte[1024 * 1024 + index * 4093 + 17];
            new Random(24680 + index).NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(source, $"payload-{index}.bin"), payload);
        }

        var destinations = Enumerable.Range(0, Math.Max(3, Math.Min(6, Environment.ProcessorCount)))
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);
    }

'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')
