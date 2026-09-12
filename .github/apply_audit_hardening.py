from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

# Make the governors internal so cancellation/accounting invariants can be regression-tested.
s = s.replace('    private sealed class PipelineGovernor\n', '    internal sealed class PipelineGovernor\n', 1)
s = s.replace('    private sealed class ResourceGovernor : IDisposable\n', '    internal sealed class ResourceGovernor : IDisposable\n', 1)
s = s.replace('    private sealed class AdaptiveByteBudget\n', '    internal sealed class AdaptiveByteBudget\n', 1)
s = s.replace('        private AdaptiveByteBudget(long initialBytes, long maximumBytes)\n', '        internal AdaptiveByteBudget(long initialBytes, long maximumBytes)\n', 1)
s = s.replace('    private sealed class SharedBlock\n', '    internal sealed class SharedBlock\n', 1)

old_shared_release = '''        public void Release()\n        {\n            if (Interlocked.Decrement(ref _references) != 0) return;\n            var buffer = Interlocked.Exchange(ref _buffer, null);\n            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);\n            _budget.Release(_reservedBytes);\n        }\n'''
new_shared_release = '''        public void Release()\n        {\n            var remaining = Interlocked.Decrement(ref _references);\n            if (remaining > 0)\n                return;\n            if (remaining < 0)\n                throw new InvalidOperationException("SharedBlock liberado más veces que referencias asignadas.");\n\n            var buffer = Interlocked.Exchange(ref _buffer, null)\n                ?? throw new InvalidOperationException("SharedBlock perdió su buffer antes de la última liberación.");\n            ArrayPool<byte>.Shared.Return(buffer);\n            _budget.Release(_reservedBytes);\n        }\n'''
if old_shared_release not in s:
    raise SystemExit('SharedBlock release anchor not found')
s = s.replace(old_shared_release, new_shared_release, 1)

# Replace PipelineGovernor with a cancellation-safe lease gate. A cancelled waiter that raced
# with a grant returns the slot immediately instead of leaking effective prefetch capacity.
start = s.index('    internal sealed class PipelineGovernor\n')
end = s.index('    internal sealed class ResourceGovernor : IDisposable\n', start)
pipeline = r'''    internal sealed class PipelineGovernor
    {
        private const int MinPrefetch = 1;
        private const int InitialPrefetch = 2;
        private const int MaxPrefetch = SourcePrefetchPhysicalCapacity;
        private const int SamplesPerDecision = 8;

        private readonly object _gate = new();
        private readonly Queue<PrefetchWaiter> _slotWaiters = new();
        private int _prefetchLimit = InitialPrefetch;
        private int _inFlight;
        private int _samples;
        private double _consumerWaitMs;
        private double _deliveryWaitMs;
        private double _budgetWaitMs;
        private double _readMs;

        internal int InFlight
        {
            get { lock (_gate) return _inFlight; }
        }

        public ValueTask AcquirePrefetchSlotAsync(CancellationToken token)
        {
            lock (_gate)
            {
                if (_inFlight < _prefetchLimit && _slotWaiters.Count == 0)
                {
                    _inFlight++;
                    return ValueTask.CompletedTask;
                }

                var waiter = new PrefetchWaiter();
                _slotWaiters.Enqueue(waiter);
                return new ValueTask(WaitForPrefetchSlotAsync(waiter, token));
            }
        }

        private async Task WaitForPrefetchSlotAsync(PrefetchWaiter waiter, CancellationToken token)
        {
            try
            {
                await waiter.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                lock (_gate)
                {
                    if (waiter.Granted)
                    {
                        waiter.Granted = false;
                        if (_inFlight <= 0)
                            throw new InvalidOperationException("Contabilidad de prefetch inválida durante cancelación.");
                        _inFlight--;
                    }
                    else
                    {
                        waiter.Cancelled = true;
                    }
                    PumpSlotsLocked();
                }
                throw;
            }
        }

        public void ReleasePrefetchSlot()
        {
            lock (_gate)
            {
                if (_inFlight <= 0)
                    throw new InvalidOperationException("Se intentó liberar un slot de prefetch no adquirido.");
                _inFlight--;
                EvaluateLocked();
                PumpSlotsLocked();
            }
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
            PumpSlotsLocked();
        }

        private void PumpSlotsLocked()
        {
            while (_inFlight < _prefetchLimit && _slotWaiters.Count > 0)
            {
                var waiter = _slotWaiters.Dequeue();
                if (waiter.Cancelled)
                    continue;
                waiter.Granted = true;
                _inFlight++;
                waiter.Ready.TrySetResult();
            }
        }

        private sealed class PrefetchWaiter
        {
            public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Cancelled { get; set; }
            public bool Granted { get; set; }
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
s = s[:start] + pipeline + s[end:]

# Harden CPU governor against grant/cancel races.
s = s.replace('        private bool _disposed;\n\n        public ResourceGovernor()\n', '''        private bool _disposed;\n\n        internal int Active\n        {\n            get { lock (_gate) return _active; }\n        }\n\n        public ResourceGovernor()\n''', 1)
old_cpu_catch = '''            catch\n            {\n                lock (_gate)\n                {\n                    waiter.Cancelled = true;\n                    PumpLocked();\n                }\n                throw;\n            }\n'''
new_cpu_catch = '''            catch\n            {\n                lock (_gate)\n                {\n                    if (waiter.Granted)\n                    {\n                        waiter.Granted = false;\n                        if (_active <= 0)\n                            throw new InvalidOperationException("Contabilidad de CPU inválida durante cancelación.");\n                        _active--;\n                    }\n                    else\n                    {\n                        waiter.Cancelled = true;\n                    }\n                    PumpLocked();\n                }\n                throw;\n            }\n'''
if old_cpu_catch not in s:
    raise SystemExit('CPU cancellation anchor not found')
s = s.replace(old_cpu_catch, new_cpu_catch, 1)
old_cpu_pump = '''                if (waiter.Cancelled)\n                    continue;\n                _active++;\n                waiter.Ready.TrySetResult();\n'''
new_cpu_pump = '''                if (waiter.Cancelled)\n                    continue;\n                waiter.Granted = true;\n                _active++;\n                waiter.Ready.TrySetResult();\n'''
if old_cpu_pump not in s:
    raise SystemExit('CPU pump anchor not found')
s = s.replace(old_cpu_pump, new_cpu_pump, 1)
s = s.replace('            public bool Cancelled { get; set; }\n        }\n\n        internal sealed class CpuLease', '            public bool Cancelled { get; set; }\n            public bool Granted { get; set; }\n        }\n\n        internal sealed class CpuLease', 1)

# Harden byte budget accounting and grant/cancel races.
s = s.replace('        private long _usedBytes;\n\n        internal AdaptiveByteBudget', '''        private long _usedBytes;\n\n        internal long UsedBytes\n        {\n            get { lock (_gate) return _usedBytes; }\n        }\n\n        internal AdaptiveByteBudget''', 1)
old_budget_release = '''            lock (_gate)\n            {\n                _usedBytes = Math.Max(0, _usedBytes - bytes);\n                ready = PumpWaitersLocked();\n            }\n'''
new_budget_release = '''            lock (_gate)\n            {\n                if (_usedBytes < bytes)\n                    throw new InvalidOperationException("Se intentó liberar más memoria FAN-OUT de la reservada.");\n                _usedBytes -= bytes;\n                ready = PumpWaitersLocked();\n            }\n'''
if old_budget_release not in s:
    raise SystemExit('budget release anchor not found')
s = s.replace(old_budget_release, new_budget_release, 1)
old_budget_catch = '''            catch\n            {\n                List<Waiter>? ready = null;\n                lock (_gate)\n                {\n                    waiter.Cancelled = true;\n                    ready = PumpWaitersLocked();\n                }\n                Complete(ready);\n                throw;\n            }\n'''
new_budget_catch = '''            catch\n            {\n                List<Waiter>? ready = null;\n                lock (_gate)\n                {\n                    if (waiter.Granted)\n                    {\n                        waiter.Granted = false;\n                        if (_usedBytes < waiter.Bytes)\n                            throw new InvalidOperationException("Contabilidad de memoria inválida durante cancelación.");\n                        _usedBytes -= waiter.Bytes;\n                    }\n                    else\n                    {\n                        waiter.Cancelled = true;\n                    }\n                    ready = PumpWaitersLocked();\n                }\n                Complete(ready);\n                throw;\n            }\n'''
if old_budget_catch not in s:
    raise SystemExit('budget cancellation anchor not found')
s = s.replace(old_budget_catch, new_budget_catch, 1)
old_budget_pump = '''                if (!TryAcquireLocked(waiter.Bytes))\n                    break;\n                _waiters.Dequeue();\n                (ready ??= []).Add(waiter);\n'''
new_budget_pump = '''                if (!TryAcquireLocked(waiter.Bytes))\n                    break;\n                _waiters.Dequeue();\n                waiter.Granted = true;\n                (ready ??= []).Add(waiter);\n'''
if old_budget_pump not in s:
    raise SystemExit('budget pump anchor not found')
s = s.replace(old_budget_pump, new_budget_pump, 1)
s = s.replace('            public bool Cancelled { get; set; }\n        }\n\n        [StructLayout', '            public bool Cancelled { get; set; }\n            public bool Granted { get; set; }\n        }\n\n        [StructLayout', 1)

engine.write_text(s, encoding='utf-8')

# Add direct invariant regressions through InternalsVisibleTo plus an end-to-end cancel stress.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]\n    public async Task AdaptivePipelinePrefetchPreservesExactLargeFileFanOut()\n'''
if anchor not in t:
    raise SystemExit('test anchor not found')
new_tests = '''    [TestMethod]\n    public async Task PipelineGovernorCancellationDoesNotLeakPrefetchCapacity()\n    {\n        var governor = new CopyEngine.PipelineGovernor();\n        await governor.AcquirePrefetchSlotAsync(CancellationToken.None);\n        await governor.AcquirePrefetchSlotAsync(CancellationToken.None);\n\n        using var cancel = new CancellationTokenSource();\n        var blocked = governor.AcquirePrefetchSlotAsync(cancel.Token).AsTask();\n        cancel.Cancel();\n        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await blocked);\n\n        governor.ReleasePrefetchSlot();\n        await governor.AcquirePrefetchSlotAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));\n        Assert.AreEqual(2, governor.InFlight);\n        governor.ReleasePrefetchSlot();\n        governor.ReleasePrefetchSlot();\n        Assert.AreEqual(0, governor.InFlight);\n    }\n\n    [TestMethod]\n    public async Task AdaptiveByteBudgetCancellationReturnsGrantedBytes()\n    {\n        var budget = new CopyEngine.AdaptiveByteBudget(64, 64);\n        await budget.AcquireAsync(64, CancellationToken.None);\n\n        using var cancel = new CancellationTokenSource();\n        var blocked = budget.AcquireAsync(64, cancel.Token).AsTask();\n        cancel.Cancel();\n        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await blocked);\n\n        budget.Release(64);\n        Assert.AreEqual(0L, budget.UsedBytes);\n        await budget.AcquireAsync(64, CancellationToken.None);\n        Assert.AreEqual(64L, budget.UsedBytes);\n        budget.Release(64);\n        Assert.AreEqual(0L, budget.UsedBytes);\n    }\n\n    [TestMethod]\n    public void SharedBlockRejectsReferenceOverRelease()\n    {\n        var budget = new CopyEngine.AdaptiveByteBudget(64, 64);\n        budget.AcquireAsync(64, CancellationToken.None).GetAwaiter().GetResult();\n        var buffer = ArrayPool<byte>.Shared.Rent(64);\n        var block = new CopyEngine.SharedBlock(buffer, 64, 64, 1, budget);\n        block.Release();\n        Assert.AreEqual(0L, budget.UsedBytes);\n        Assert.ThrowsExactly<InvalidOperationException>(() => block.Release());\n    }\n\n    [TestMethod]\n    public async Task ResourceGovernorCancelledWaiterDoesNotConsumeCpuLease()\n    {\n        using var governor = new CopyEngine.ResourceGovernor();\n        using var first = await governor.EnterCpuWorkAsync(CancellationToken.None);\n        using var cancel = new CancellationTokenSource();\n        var blocked = governor.EnterCpuWorkAsync(cancel.Token).AsTask();\n        cancel.Cancel();\n        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await blocked);\n        Assert.AreEqual(1, governor.Active);\n    }\n\n'''
t = t.replace(anchor, new_tests + anchor, 1)
# Tests now use ArrayPool explicitly.
if 'using System.Buffers;\n' not in t:
    t = t.replace('using System.Text;\n', 'using System.Buffers;\nusing System.Text;\n', 1)
tests.write_text(t, encoding='utf-8')
