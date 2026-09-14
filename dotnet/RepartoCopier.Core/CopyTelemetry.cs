using System.Diagnostics;
using System.Threading;

namespace RepartoCopier.Core;

public sealed record PipelineGovernorSnapshot(
    int CurrentPrefetchLimit,
    int MinimumObservedPrefetchLimit,
    int MaximumObservedPrefetchLimit,
    int InFlight,
    int DecisionCount,
    int Upshifts,
    int Downshifts,
    string LastDecision,
    TimeSpan ConsumerWaitTime,
    TimeSpan DeliveryWaitTime,
    TimeSpan BudgetWaitTime,
    TimeSpan SourceReadTime);

public sealed record CopyDiagnosticsSnapshot(
    long SourceReadBytes,
    TimeSpan SourceReadTime,
    long SourceHashBytes,
    TimeSpan SourceHashTime,
    TimeSpan BufferWaitTime,
    TimeSpan FanoutWaitTime,
    long WrittenBytes,
    long WriteOperations,
    TimeSpan WriteTime,
    int DurableFlushes,
    TimeSpan DurableFlushTime,
    int Commits,
    TimeSpan CommitTime,
    int RecoveryEvents,
    TimeSpan RecoveryTime,
    long VerifyReadBytes,
    TimeSpan VerifyReadTime,
    long VerifyHashBytes,
    TimeSpan VerifyHashTime,
    long PeakBufferedBytes,
    long MaximumObservedBufferTargetBytes,
    TimeSpan CopyPhaseElapsed,
    TimeSpan VerifyPhaseElapsed,
    TimeSpan Elapsed)
{
    public double SourceReadBytesPerSecond => Rate(SourceReadBytes, SourceReadTime);
    public double SourceHashBytesPerSecond => Rate(SourceHashBytes, SourceHashTime);
    public double WriteBytesPerSecond => Rate(WrittenBytes, WriteTime);
    public double VerifyReadBytesPerSecond => Rate(VerifyReadBytes, VerifyReadTime);
    public double VerifyHashBytesPerSecond => Rate(VerifyHashBytes, VerifyHashTime);
    public double SourceReadWallClockBytesPerSecond => Rate(SourceReadBytes, CopyPhaseElapsed);
    public double FanoutLogicalWriteWallClockBytesPerSecond => Rate(WrittenBytes, CopyPhaseElapsed);
    public double SustainedWrite5sBytesPerSecond { get; init; }
    public double SustainedWrite10sBytesPerSecond { get; init; }
    public IReadOnlyList<DeviceIoSnapshot> DeviceSchedulers { get; init; } = [];
    public PipelineGovernorSnapshot? PipelineGovernor { get; init; }
    public long DirectSourceReadBytes { get; init; }
    public long DirectSourceReadOperations { get; init; }
    public int DirectSourceFallbacks { get; init; }
    public int DirectDestinationFiles { get; init; }
    public long DirectDestinationWriteBytes { get; init; }
    public long DirectDestinationWriteOperations { get; init; }
    public int DirectDestinationFallbacks { get; init; }
    public long VerificationReadBudgetBytes { get; init; }
    public long PeakVerificationReadBytes { get; init; }

    private static double Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes / elapsed.TotalSeconds;
}

internal sealed class CopyTelemetry
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly SlidingByteRateWindow _writeRate = new();
    private IReadOnlyCollection<DeviceScheduler>? _deviceSchedulers;
    private Func<PipelineGovernorSnapshot>? _pipelineGovernorSnapshot;
    private long _sourceReadBytes, _sourceReadTicks;
    private long _directSourceReadBytes, _directSourceReadOperations;
    private int _directSourceFallbacks;
    private int _directDestinationFiles, _directDestinationFallbacks;
    private long _directDestinationWriteBytes, _directDestinationWriteOperations;
    private long _sourceHashBytes, _sourceHashTicks;
    private long _bufferWaitTicks, _fanoutWaitTicks;
    private long _writtenBytes, _writeOperations, _writeTicks;
    private int _flushes, _commits, _recoveryEvents;
    private long _flushTicks, _commitTicks, _recoveryTicks;
    private long _verifyReadBytes, _verifyReadTicks;
    private long _verifyHashBytes, _verifyHashTicks;
    private long _verificationReadBudgetBytes, _peakVerificationReadBytes;
    private long _peakBufferedBytes, _maxObservedBufferTargetBytes;
    private long _copyPhaseTicks, _verifyPhaseTicks;

    internal void AttachDeviceSchedulers(IReadOnlyCollection<DeviceScheduler> schedulers) =>
        _deviceSchedulers = schedulers.ToArray();

    internal void AttachPipelineGovernor(Func<PipelineGovernorSnapshot> snapshotProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        _pipelineGovernorSnapshot = snapshotProvider;
    }

    internal void RecordSourceRead(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceReadBytes, bytes); AddTicks(ref _sourceReadTicks, elapsed); }
    internal void RecordDirectSourceRead(int bytes) { AddBytes(ref _directSourceReadBytes, bytes); Interlocked.Increment(ref _directSourceReadOperations); }
    internal void RecordDirectSourceFallback() => Interlocked.Increment(ref _directSourceFallbacks);
    internal void RecordDirectDestinationFile() => Interlocked.Increment(ref _directDestinationFiles);
    internal void RecordDirectDestinationWrite(int bytes, int operations)
    {
        AddBytes(ref _directDestinationWriteBytes, bytes);
        if (operations > 0) Interlocked.Add(ref _directDestinationWriteOperations, operations);
    }
    internal void RecordDirectDestinationFallback() => Interlocked.Increment(ref _directDestinationFallbacks);
    internal void RecordSourceHash(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceHashBytes, bytes); AddTicks(ref _sourceHashTicks, elapsed); }
    internal void RecordBufferWait(TimeSpan elapsed) => AddTicks(ref _bufferWaitTicks, elapsed);
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);

    internal void RecordWrite(int bytes, TimeSpan elapsed)
    {
        AddBytes(ref _writtenBytes, bytes);
        AddTicks(ref _writeTicks, elapsed);

        _writeRate.Record(bytes);
    }

    internal void RecordWriteOperation() => Interlocked.Increment(ref _writeOperations);
    internal void RecordFlush(TimeSpan elapsed) { Interlocked.Increment(ref _flushes); AddTicks(ref _flushTicks, elapsed); }

    internal void RecordCommit(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _commits);
        AddTicks(ref _commitTicks, elapsed);
    }

    internal void RecordRecovery(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _recoveryEvents);
        AddTicks(ref _recoveryTicks, elapsed);
    }

    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }
    internal void RecordVerifyHash(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyHashBytes, bytes); AddTicks(ref _verifyHashTicks, elapsed); }
    internal void RecordVerificationBufferBudget(long budgetBytes, long peakBytes)
    {
        if (budgetBytes > 0) Interlocked.Exchange(ref _verificationReadBudgetBytes, budgetBytes);
        if (peakBytes > 0) UpdateMax(ref _peakVerificationReadBytes, peakBytes);
    }
    internal void RecordCopyPhase(TimeSpan elapsed) => AddTicks(ref _copyPhaseTicks, elapsed);
    internal void RecordVerifyPhase(TimeSpan elapsed) => AddTicks(ref _verifyPhaseTicks, elapsed);

    internal void ObserveBuffer(long usedBytes, long targetBytes)
    {
        UpdateMax(ref _peakBufferedBytes, usedBytes);
        UpdateMax(ref _maxObservedBufferTargetBytes, targetBytes);
    }

    internal CopyDiagnosticsSnapshot Snapshot()
    {
        var sustained = _writeRate.Snapshot();

        var devices = _deviceSchedulers?
            .Select(item => item.Snapshot())
            .OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var pipeline = _pipelineGovernorSnapshot?.Invoke();

        return new CopyDiagnosticsSnapshot(
            Interlocked.Read(ref _sourceReadBytes), ToTimeSpan(Interlocked.Read(ref _sourceReadTicks)),
            Interlocked.Read(ref _sourceHashBytes), ToTimeSpan(Interlocked.Read(ref _sourceHashTicks)),
            ToTimeSpan(Interlocked.Read(ref _bufferWaitTicks)),
            ToTimeSpan(Interlocked.Read(ref _fanoutWaitTicks)),
            Interlocked.Read(ref _writtenBytes), Interlocked.Read(ref _writeOperations), ToTimeSpan(Interlocked.Read(ref _writeTicks)),
            Volatile.Read(ref _flushes), ToTimeSpan(Interlocked.Read(ref _flushTicks)),
            Volatile.Read(ref _commits), ToTimeSpan(Interlocked.Read(ref _commitTicks)),
            Volatile.Read(ref _recoveryEvents), ToTimeSpan(Interlocked.Read(ref _recoveryTicks)),
            Interlocked.Read(ref _verifyReadBytes), ToTimeSpan(Interlocked.Read(ref _verifyReadTicks)),
            Interlocked.Read(ref _verifyHashBytes), ToTimeSpan(Interlocked.Read(ref _verifyHashTicks)),
            Interlocked.Read(ref _peakBufferedBytes), Interlocked.Read(ref _maxObservedBufferTargetBytes),
            ToTimeSpan(Interlocked.Read(ref _copyPhaseTicks)),
            ToTimeSpan(Interlocked.Read(ref _verifyPhaseTicks)),
            Stopwatch.GetElapsedTime(_started))
        {
            SustainedWrite5sBytesPerSecond = sustained.FiveSecondsBytesPerSecond,
            SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,
            DeviceSchedulers = devices,
            PipelineGovernor = pipeline,
            DirectSourceReadBytes = Interlocked.Read(ref _directSourceReadBytes),
            DirectSourceReadOperations = Interlocked.Read(ref _directSourceReadOperations),
            DirectSourceFallbacks = Volatile.Read(ref _directSourceFallbacks),
            DirectDestinationFiles = Volatile.Read(ref _directDestinationFiles),
            DirectDestinationWriteBytes = Interlocked.Read(ref _directDestinationWriteBytes),
            DirectDestinationWriteOperations = Interlocked.Read(ref _directDestinationWriteOperations),
            DirectDestinationFallbacks = Volatile.Read(ref _directDestinationFallbacks),
            VerificationReadBudgetBytes = Interlocked.Read(ref _verificationReadBudgetBytes),
            PeakVerificationReadBytes = Interlocked.Read(ref _peakVerificationReadBytes),
        };
    }

    private static void AddBytes(ref long target, int bytes) { if (bytes > 0) Interlocked.Add(ref target, bytes); }
    private static void AddTicks(ref long target, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        var ticks = (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
        if (ticks > 0) Interlocked.Add(ref target, ticks);
    }
    private static TimeSpan ToTimeSpan(long ticks) => ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    private static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
