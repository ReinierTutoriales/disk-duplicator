from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

# Process priority support.
if 'using System.Diagnostics;\n' not in s:
    s = s.replace('using System.Collections.Concurrent;\n', 'using System.Collections.Concurrent;\nusing System.Diagnostics;\n', 1)

# Remove static verification parallelism constant; the governor computes it from CPU + active destinations.
s = s.replace('    private const int MaxVerificationParallelism = 8;\n', '    private const int AbsoluteMaxVerificationParallelism = 8;\n', 1)

# Writers are already async and RunAsync itself executes on a background Task.Run from Start().
old_writers = '''            var writerTasks = workers
                .Select(worker => Task.Run(() => WriterLoopAsync(worker, options, job, expectedHashes), CancellationToken.None))
                .ToArray();
'''
new_writers = '''            using var copyResources = ResourceGovernor.EnterCopy(workers.Length);
            var writerTasks = workers
                .Select(worker => WriterLoopAsync(worker, options, job, expectedHashes))
                .ToArray();
'''
if old_writers not in s:
    raise SystemExit('writerTasks anchor not found')
s = s.replace(old_writers, new_writers, 1)

# Restore normal/original process priority before the verification phase.
old_verify = '''            if (writerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();

            if (options.Verify && !token.IsCancellationRequested)
                await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job).ConfigureAwait(false);
'''
new_verify = '''            if (writerError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(writerError).Throw();

            copyResources.Dispose();
            if (options.Verify && !token.IsCancellationRequested)
                await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job).ConfigureAwait(false);
'''
if old_verify not in s:
    raise SystemExit('verification phase anchor not found')
s = s.replace(old_verify, new_verify, 1)

s = s.replace(
    'MaxDegreeOfParallelism = Math.Min(MaxVerificationParallelism, Math.Max(1, activeSlots.Length)),',
    'MaxDegreeOfParallelism = ResourceGovernor.VerificationParallelism(activeSlots.Length),',
    1)
s = s.replace(
    'MaxDegreeOfParallelism = Math.Min(MaxVerificationParallelism, candidates.Count),',
    'MaxDegreeOfParallelism = ResourceGovernor.VerificationParallelism(candidates.Count),',
    1)

# Insert ResourceGovernor before AdaptiveByteBudget so resource policy is centralized near other engine governors.
anchor = '    private sealed class AdaptiveByteBudget\n'
idx = s.find(anchor)
if idx < 0:
    raise SystemExit('AdaptiveByteBudget anchor not found')

governor = r'''    private static class ResourceGovernor
    {
        private static readonly object PriorityGate = new();
        private static int _activeCopyLeases;
        private static ProcessPriorityClass? _originalPriority;

        public static CopyLease EnterCopy(int destinations)
        {
            EnsureThreadPoolCapacity(destinations);
            lock (PriorityGate)
            {
                _activeCopyLeases++;
                if (_activeCopyLeases == 1)
                    RaiseProcessPriorityForCopy();
            }
            return new CopyLease();
        }

        public static int VerificationParallelism(int activeDestinations)
        {
            if (activeDestinations <= 0)
                return 1;
            var cpuBound = Math.Max(1, Environment.ProcessorCount / 2);
            return Math.Min(activeDestinations, Math.Min(AbsoluteMaxVerificationParallelism, cpuBound));
        }

        private static void EnsureThreadPoolCapacity(int destinations)
        {
            var logicalProcessors = Math.Max(1, Environment.ProcessorCount);
            var destinationDemand = Math.Max(1, destinations) + 4;
            var targetWorkers = Math.Min(
                64,
                Math.Max(logicalProcessors, Math.Min(destinationDemand, logicalProcessors * 2)));
            ThreadPool.GetMinThreads(out var currentWorkers, out var currentIo);
            if (targetWorkers > currentWorkers)
                _ = ThreadPool.SetMinThreads(targetWorkers, currentIo);
        }

        private static void RaiseProcessPriorityForCopy()
        {
            if (!OperatingSystem.IsWindows())
                return;
            try
            {
                using var process = Process.GetCurrentProcess();
                var current = process.PriorityClass;
                _originalPriority = current;
                if (current is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal)
                    process.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Priority is an optimization only. Copy correctness must never depend on it.
                _originalPriority = null;
            }
        }

        private static void ExitCopy()
        {
            lock (PriorityGate)
            {
                if (_activeCopyLeases <= 0)
                    return;
                _activeCopyLeases--;
                if (_activeCopyLeases != 0)
                    return;
                RestoreProcessPriority();
            }
        }

        private static void RestoreProcessPriority()
        {
            var original = _originalPriority;
            _originalPriority = null;
            if (original is null || !OperatingSystem.IsWindows())
                return;
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = original.Value;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // Best effort. Never fail a completed copy because Windows rejected a priority change.
            }
        }

        internal sealed class CopyLease : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                ExitCopy();
            }
        }
    }

'''
s = s[:idx] + governor + s[idx:]
engine.write_text(s, encoding='utf-8')

# Regression: exercise 10 destination writers + verification on the resource-governed path.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task FanOutVerifiesEightDestinationsConcurrently()
'''
if anchor not in t:
    raise SystemExit('10-destination test anchor not found')
new_test = '''    [TestMethod]
    public async Task FanOutTenDestinationsRemainExactWithoutWorkerStarvation()
    {
        using var temp = new TempDirectory("fanout-ten-destinations");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[2 * 1024 * 1024 + 113];
        new Random(13579).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var destinations = Enumerable.Range(0, 10)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();

        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);

        foreach (var destination in destinations)
            CollectionAssert.AreEqual(
                payload,
                await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "payload.bin")));
    }

'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')
