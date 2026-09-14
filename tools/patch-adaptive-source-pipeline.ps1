$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: patrón exacto no encontrado" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$copy = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$tests = 'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs'

Replace-Exact $copy @'
    private const int BlockSize = 32 * 1024 * 1024;
    private const int SourcePrefetchPhysicalCapacity = 8;
    private const int SourceHashPipelineCapacity = 4;
    private const int SourcePrefetchThreshold = 16 * 1024 * 1024;
'@ @'
    private const int BlockSize = 32 * 1024 * 1024;
    private const int SourcePrefetchThreshold = 16 * 1024 * 1024;
'@ 'eliminar caps físicos prefetch/hash'

Replace-Exact $copy @'
    private const long MinimumBufferBudget = 256L * 1024 * 1024;
    private const long InitialBufferBudget = 512L * 1024 * 1024;
    private const long MaximumBufferBudget = 4L * 1024 * 1024 * 1024;
    private const long BufferBudgetGrowthStep = 256L * 1024 * 1024;
    // 4 GiB / 32 MiB = 128 maximum live data blocks. At the public
    // 256-destination ceiling that is 65,536 channel references. The same
    // global budget also bounds Begin/End-heavy trees whose payload-byte
    // budget would otherwise see almost no pressure.
    private const int ControlBacklogCapacity = 64 * 1024;
'@ @'
    private const long InitialBufferBudget = 512L * 1024 * 1024;
    private const long BufferBudgetGrowthStep = 256L * 1024 * 1024;
    // This budget protects Begin/End-heavy trees. Payload data is independently
    // governed by AdaptiveByteBudget and per-device backlog/QD.
    private const int ControlBacklogCapacity = 64 * 1024;
'@ 'eliminar máximo fijo de buffer'

Replace-Exact $copy @'
        using var resources = new ResourceGovernor();
        var pipeline = new PipelineGovernor();
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
'@ @'
        using var resources = new ResourceGovernor();
        var bufferBudget = AdaptiveByteBudget.CreateForSystem();
        var pipeline = new PipelineGovernor(bufferBudget, BlockSize);
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
'@ 'crear budget antes del pipeline'

Replace-Exact $copy @'
                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job, pipeline).ConfigureAwait(false);
'@ @'
                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job, pipeline, bufferBudget).ConfigureAwait(false);
'@ 'pasar budget de producción'

Replace-Exact $copy @'
        CopyJob job,
        PipelineGovernor pipeline)
    {
        var token = job.Token;
        var bufferBudget = AdaptiveByteBudget.CreateForSystem();
        try
'@ @'
        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget)
    {
        var token = job.Token;
        try
'@ 'unificar budget del productor'

Replace-Exact $copy @'
        var sourceQueue = Channel.CreateBounded<SourceReadBlock>(new BoundedChannelOptions(SourcePrefetchPhysicalCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
'@ @'
        var sourceQueue = Channel.CreateUnbounded<SourceReadBlock>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
'@ 'quitar cap fijo del canal prefetch'

Replace-Exact $copy @'
        var hashQueue = Channel.CreateBounded<SourceReadBlock>(new BoundedChannelOptions(SourceHashPipelineCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
'@ @'
        var hashQueue = Channel.CreateUnbounded<SourceReadBlock>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
'@ 'quitar cap fijo del canal hash'

Replace-Exact $copy @'
    internal sealed class PipelineGovernor
    {
        private const int MinPrefetch = 1;
        private const int InitialPrefetch = 4;
        private const int MaxPrefetch = SourcePrefetchPhysicalCapacity;
        private const int SamplesPerDecision = 8;

        private readonly object _gate = new();
        private readonly Queue<PrefetchWaiter> _slotWaiters = new();
        private int _prefetchLimit = InitialPrefetch;
        private int _minimumObservedPrefetchLimit = InitialPrefetch;
        private int _maximumObservedPrefetchLimit = InitialPrefetch;
'@ @'
    internal sealed class PipelineGovernor
    {
        private const int MinPrefetch = 1;
        private const int InitialPrefetch = 4;
        private const int SamplesPerDecision = 8;

        private readonly object _gate = new();
        private readonly Queue<PrefetchWaiter> _slotWaiters = new();
        private readonly AdaptiveByteBudget _budget;
        private readonly int _bytesPerBlock;
        private int _prefetchLimit;
        private int _minimumObservedPrefetchLimit;
        private int _maximumObservedPrefetchLimit;
'@ 'hacer governor dependiente de memoria'

Replace-Exact $copy @'
        private double _totalBudgetWaitMs;
        private double _totalReadMs;

        internal int InFlight
'@ @'
        private double _totalBudgetWaitMs;
        private double _totalReadMs;

        internal PipelineGovernor(AdaptiveByteBudget budget, int bytesPerBlock)
        {
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerBlock);
            _bytesPerBlock = bytesPerBlock;
            _prefetchLimit = Math.Min(InitialPrefetch, CurrentCapacityLocked());
            _minimumObservedPrefetchLimit = _prefetchLimit;
            _maximumObservedPrefetchLimit = _prefetchLimit;
        }

        internal int InFlight
'@ 'constructor dinámico del governor'

Replace-Exact $copy @'
        public ValueTask AcquirePrefetchSlotAsync(CancellationToken token)
        {
            lock (_gate)
            {
                if (_inFlight < _prefetchLimit && _slotWaiters.Count == 0)
'@ @'
        public ValueTask AcquirePrefetchSlotAsync(CancellationToken token)
        {
            lock (_gate)
            {
                var capacity = CurrentCapacityLocked();
                if (_prefetchLimit > capacity)
                    _prefetchLimit = capacity;
                if (_inFlight < _prefetchLimit && _slotWaiters.Count == 0)
'@ 'recalcular capacidad al adquirir'

Replace-Exact $copy @'
            var previous = _prefetchLimit;
            var decision = "hold:balanced";

            if (pressure > starvation * 1.5 && pressure > sourceCost)
            {
                _prefetchLimit = Math.Max(MinPrefetch, _prefetchLimit - 1);
                decision = _prefetchLimit < previous ? "decrease:pressure" : "hold:min";
            }
            else if (starvation > pressure * 1.5 && starvation > sourceCost * 0.25)
            {
                _prefetchLimit = Math.Min(MaxPrefetch, _prefetchLimit + 1);
                decision = _prefetchLimit > previous ? "increase:starvation" : "hold:max";
            }
'@ @'
            var previous = _prefetchLimit;
            var capacity = CurrentCapacityLocked();
            var decision = "hold:balanced";

            if (pressure > starvation * 1.5 && pressure > sourceCost)
            {
                _prefetchLimit = Math.Max(MinPrefetch, _prefetchLimit / 2);
                decision = _prefetchLimit < previous ? "decrease:pressure" : "hold:min";
            }
            else if (starvation > pressure * 1.5 && starvation > sourceCost * 0.25)
            {
                var doubled = previous > int.MaxValue / 2 ? int.MaxValue : previous * 2;
                _prefetchLimit = Math.Min(capacity, Math.Max(previous + 1, doubled));
                decision = _prefetchLimit > previous ? "increase:starvation" : "hold:memory";
            }
            else if (_prefetchLimit > capacity)
            {
                _prefetchLimit = capacity;
                decision = "decrease:memory";
            }
'@ 'crecimiento multiplicativo del prefetch'

Replace-Exact $copy @'
        private void PumpSlotsLocked()
        {
            while (_inFlight < _prefetchLimit && _slotWaiters.Count > 0)
'@ @'
        private int CurrentCapacityLocked() =>
            Math.Max(MinPrefetch, _budget.GetAdmissibleConcurrency(_bytesPerBlock));

        private void PumpSlotsLocked()
        {
            var capacity = CurrentCapacityLocked();
            if (_prefetchLimit > capacity)
                _prefetchLimit = capacity;
            while (_inFlight < _prefetchLimit && _slotWaiters.Count > 0)
'@ 'capacidad derivada de memoria'

Replace-Exact $copy @'
    internal sealed class AdaptiveByteBudget
    {
        private readonly object _gate = new();
        private readonly Queue<Waiter> _waiters = new();
        private readonly long _maximumBytes;
        private long _targetBytes;
        private long _usedBytes;
'@ @'
    internal sealed class AdaptiveByteBudget
    {
        private readonly object _gate = new();
        private readonly Queue<Waiter> _waiters = new();
        private readonly Func<long, long> _capacityProvider;
        private long _targetBytes;
        private long _usedBytes;
'@ 'quitar campo maximumBytes'

Replace-Exact $copy @'
        internal AdaptiveByteBudget(long initialBytes, long maximumBytes)
        {
            _targetBytes = initialBytes;
            _maximumBytes = maximumBytes;
        }

        public static AdaptiveByteBudget CreateForSystem()
        {
            var memory = GetMemoryStatus();
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var maximum = Math.Clamp(total / 8, MinimumBufferBudget, MaximumBufferBudget);
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var safeNow = Math.Max(MinimumBufferBudget, available - reserve);
            maximum = Math.Max(MinimumBufferBudget, Math.Min(maximum, safeNow));
            var initial = Math.Min(InitialBufferBudget, maximum);
            return new AdaptiveByteBudget(initial, maximum);
        }
'@ @'
        internal AdaptiveByteBudget(long initialBytes, long maximumBytes)
            : this(initialBytes, _ => maximumBytes)
        {
            if (maximumBytes <= 0 || initialBytes <= 0 || initialBytes > maximumBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        private AdaptiveByteBudget(long initialBytes, Func<long, long> capacityProvider)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialBytes);
            _capacityProvider = capacityProvider ?? throw new ArgumentNullException(nameof(capacityProvider));
            _targetBytes = initialBytes;
        }

        public static AdaptiveByteBudget CreateForSystem()
        {
            var safeNow = GetSystemSafeCapacity(0);
            var initial = Math.Max((long)BlockSize, Math.Min(InitialBufferBudget, safeNow));
            return new AdaptiveByteBudget(initial, GetSystemSafeCapacity);
        }

        internal int GetAdmissibleConcurrency(int bytesPerBlock)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerBlock);
            lock (_gate)
            {
                var capacity = CurrentSafeCapacityLocked();
                var blocks = Math.Max(1L, capacity / bytesPerBlock);
                return (int)Math.Min(int.MaxValue, blocks);
            }
        }
'@ 'budget sin techo fijo de GiB'

Replace-Exact $copy @'
        private bool TryAcquireLocked(int bytes)
        {
            if (_usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }

            if (TryGrowLocked(bytes) && _usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }
            return false;
        }

        private bool TryGrowLocked(int bytes)
        {
            if (_targetBytes >= _maximumBytes)
                return false;

            var memory = GetMemoryStatus();
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var headroom = available - reserve;
            if (headroom < BufferBudgetGrowthStep)
                return false;

            var requestedTarget = Math.Max(_targetBytes + BufferBudgetGrowthStep, _usedBytes + bytes);
            var safeTarget = _usedBytes + headroom;
            var next = Math.Min(_maximumBytes, Math.Min(requestedTarget, safeTarget));
            if (next <= _targetBytes)
                return false;
            _targetBytes = next;
            return true;
        }
'@ @'
        private bool TryAcquireLocked(int bytes)
        {
            var safeCapacity = CurrentSafeCapacityLocked();
            var effectiveTarget = Math.Min(_targetBytes, safeCapacity);
            if (_usedBytes + bytes <= effectiveTarget)
            {
                _usedBytes += bytes;
                return true;
            }

            if (TryGrowLocked(bytes, safeCapacity) && _usedBytes + bytes <= _targetBytes)
            {
                _usedBytes += bytes;
                return true;
            }
            return false;
        }

        private bool TryGrowLocked(int bytes, long safeCapacity)
        {
            if (_usedBytes + bytes > safeCapacity || _targetBytes >= safeCapacity)
                return false;

            var requestedTarget = Math.Max(
                _targetBytes > long.MaxValue - BufferBudgetGrowthStep
                    ? long.MaxValue
                    : _targetBytes + BufferBudgetGrowthStep,
                _usedBytes + bytes);
            var next = Math.Min(requestedTarget, safeCapacity);
            if (next <= _targetBytes)
                return false;
            _targetBytes = next;
            return true;
        }

        private long CurrentSafeCapacityLocked()
        {
            var capacity = _capacityProvider(_usedBytes);
            return Math.Max(_usedBytes, capacity);
        }

        private static long GetSystemSafeCapacity(long usedBytes)
        {
            var memory = GetMemoryStatus();
            var available = checked((long)Math.Min(memory.ullAvailPhys, (ulong)long.MaxValue));
            var total = checked((long)Math.Min(memory.ullTotalPhys, (ulong)long.MaxValue));
            var reserve = Math.Max(2L * 1024 * 1024 * 1024, total / 4);
            var additional = Math.Max(0L, available - reserve);
            return additional >= long.MaxValue - usedBytes ? long.MaxValue : usedBytes + additional;
        }
'@ 'crecimiento dinámico por memoria actual'

Replace-Exact $tests @'
        var governor = new CopyEngine.PipelineGovernor();
        await governor.AcquirePrefetchSlotAsync(CancellationToken.None);
'@ @'
        var pipelineBudget = new CopyEngine.AdaptiveByteBudget(4, 4);
        var governor = new CopyEngine.PipelineGovernor(pipelineBudget, 1);
        await governor.AcquirePrefetchSlotAsync(CancellationToken.None);
'@ 'actualizar test cancelación pipeline'

Replace-Exact $tests @'
        var governor = new CopyEngine.PipelineGovernor();
        for (var iteration = 0; iteration < 200; iteration++)
'@ @'
        var pipelineBudget = new CopyEngine.AdaptiveByteBudget(4, 4);
        var governor = new CopyEngine.PipelineGovernor(pipelineBudget, 1);
        for (var iteration = 0; iteration < 200; iteration++)
'@ 'actualizar stress pipeline'

$copyText = [IO.File]::ReadAllText($copy)
foreach ($forbidden in @('SourcePrefetchPhysicalCapacity', 'SourceHashPipelineCapacity', 'MaximumBufferBudget', 'MinimumBufferBudget', 'CreateBounded<SourceReadBlock>')) {
    if ($copyText.Contains($forbidden)) { throw "Símbolo/cap antiguo sigue presente: $forbidden" }
}
if (-not $copyText.Contains('GetAdmissibleConcurrency')) { throw 'Pipeline no consume capacidad de memoria' }
if (-not $copyText.Contains('CreateUnbounded<SourceReadBlock>')) { throw 'Canales de source no quedaron gobernados por budget/slots' }

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $copy $tests
if (git diff --cached --quiet) { throw 'La migración source pipeline no produjo cambios.' }
git commit -m 'perf(core): remove fixed source pipeline ceilings'
git push origin HEAD:main
