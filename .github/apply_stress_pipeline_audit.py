from pathlib import Path
import re

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

# Pause/resume must never pin ThreadPool workers. Replace ManualResetEventSlim with an async gate.
s = s.replace(
'''    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);''',
'''    private readonly AsyncPauseGate _pauseGate = new();''', 1)
s = s.replace('    public bool IsPaused => !_pauseGate.IsSet;', '    public bool IsPaused => _pauseGate.IsPaused;', 1)
s = s.replace(
'''    public void SetPaused(bool paused)
    {
        if (paused) _pauseGate.Reset();
        else _pauseGate.Set();
    }

    public void RequestCancel()
    {
        _pauseGate.Set();
        _cancel.Cancel();
    }

    internal void WaitIfPaused(CancellationToken token) => _pauseGate.Wait(token);
''',
'''    public void SetPaused(bool paused) => _pauseGate.SetPaused(paused);

    public void RequestCancel()
    {
        _pauseGate.SetPaused(false);
        _cancel.Cancel();
    }

    internal ValueTask WaitIfPausedAsync(CancellationToken token) => _pauseGate.WaitAsync(token);
''', 1)
s = s.replace('        _pauseGate.Dispose();\n        _cancel.Dispose();', '        _cancel.Dispose();', 1)

copyjob_end = '''    }
}

public static class CopyEngine
'''
if copyjob_end not in s:
    raise SystemExit('CopyJob end anchor not found')
async_gate = r'''    }

    private sealed class AsyncPauseGate
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _resumeSignal;

        public bool IsPaused
        {
            get { lock (_gate) return _resumeSignal is not null; }
        }

        public void SetPaused(bool paused)
        {
            TaskCompletionSource? resume = null;
            lock (_gate)
            {
                if (paused)
                {
                    _resumeSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                else
                {
                    resume = _resumeSignal;
                    _resumeSignal = null;
                }
            }
            resume?.TrySetResult();
        }

        public async ValueTask WaitAsync(CancellationToken token)
        {
            while (true)
            {
                Task? wait;
                lock (_gate)
                    wait = _resumeSignal?.Task;
                if (wait is null)
                    return;
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }
}

public static class CopyEngine
'''
s = s.replace(copyjob_end, async_gate, 1)

# Convert every cooperative pause safe-point in async engine code to a non-blocking wait.
s, converted = re.subn(
    r'(?m)^(?P<indent>\s*)job\.WaitIfPaused\((?P<token>[^;]+)\);$',
    lambda m: f"{m.group('indent')}await job.WaitIfPausedAsync({m.group('token')}).ConfigureAwait(false);",
    s)
if converted == 0:
    raise SystemExit('No pause safe points were converted')
if re.search(r'\bWaitIfPaused\s*\(', s):
    raise SystemExit('A synchronous pause wait remains in CopyEngine.cs')

# Prefetch adaptation should evaluate after complete consumer cycles, not after arbitrary partial events.
old = '''                _samples++;
                EvaluateLocked();
'''
new = '''                if (kind == SampleKind.Consumer)
                    _samples++;
                EvaluateLocked();
'''
if old not in s:
    raise SystemExit('Pipeline sample anchor not found')
s = s.replace(old, new, 1)

engine.write_text(s, encoding='utf-8')

# Add stress/regression coverage for async pause and repeated adaptive-gate cancellation.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task AdaptivePipelinePrefetchPreservesExactLargeFileFanOut()
'''
if anchor not in t:
    raise SystemExit('stress test anchor not found')
new_tests = r'''    [TestMethod]
    public async Task AsyncPauseGateReleasesHundredsOfWaitersWithoutBlockingThreads()
    {
        await using var job = new CopyJob(Array.Empty<DestinationProgress>());
        job.SetPaused(true);
        Assert.IsTrue(job.IsPaused);

        var waits = Enumerable.Range(0, 512)
            .Select(_ => job.WaitIfPausedAsync(CancellationToken.None).AsTask())
            .ToArray();
        Assert.AreEqual(0, waits.Count(task => task.IsCompleted));

        job.SetPaused(false);
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(job.IsPaused);
    }

    [TestMethod]
    public async Task PipelineGovernorRepeatedCancellationStressDoesNotLeakSlots()
    {
        var governor = new CopyEngine.PipelineGovernor();
        for (var iteration = 0; iteration < 200; iteration++)
        {
            await governor.AcquirePrefetchSlotAsync(CancellationToken.None);
            await governor.AcquirePrefetchSlotAsync(CancellationToken.None);

            using var cancel = new CancellationTokenSource();
            var blocked = Enumerable.Range(0, 16)
                .Select(_ => governor.AcquirePrefetchSlotAsync(cancel.Token).AsTask())
                .ToArray();
            cancel.Cancel();
            foreach (var task in blocked)
            {
                try
                {
                    await task;
                    Assert.Fail("Se esperaba cancelación.");
                }
                catch (OperationCanceledException)
                {
                }
            }

            governor.ReleasePrefetchSlot();
            governor.ReleasePrefetchSlot();
            Assert.AreEqual(0, governor.InFlight);
        }
    }

    [TestMethod]
    public async Task FanOutStressWithRapidPauseResumePreservesEveryDestination()
    {
        using var temp = new TempDirectory("fanout-pause-stress");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[24 * 1024 * 1024 + 193];
        new Random(314159).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "stress.bin"), payload);

        var destinations = Enumerable.Range(0, 8)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);

        for (var cycle = 0; cycle < 20 && !job.Completion.IsCompleted; cycle++)
        {
            job.SetPaused(true);
            await Task.Delay(5);
            job.SetPaused(false);
            await Task.Delay(5);
        }
        job.SetPaused(false);

        await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));
        AssertHealthy(job);
        foreach (var destination in destinations)
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "stress.bin")));
    }

'''
t = t.replace(anchor, new_tests + anchor, 1)
tests.write_text(t, encoding='utf-8')
