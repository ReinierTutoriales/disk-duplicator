using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace RepartoCopier.Core;

public sealed record IoRecoveryEvent(
    DateTimeOffset Timestamp,
    string Phase,
    string Path,
    string Mode,
    int NativeErrorCode,
    int QueueDepth,
    int RetryCount,
    long Offset,
    bool Recovered);

public enum VerificationBottleneckKind
{
    None,
    StorageRead,
    Crc32C,
    Balanced,
}
public sealed record CopyDiagnosticsSnapshot(
    long SourceReadBytes,
    TimeSpan SourceReadTime,
    long SourceHashBytes,
    TimeSpan SourceHashTime,
    TimeSpan BufferWaitTime,
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
    long VerifyCrc32CBytes,
    TimeSpan VerifyCrc32CTime,
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
    public double VerifyCrc32CBytesPerSecond => Rate(VerifyCrc32CBytes, VerifyCrc32CTime);

    public VerificationBottleneckKind VerificationBottleneck => ClassifyVerificationBottleneck(
        VerifyReadBytesPerSecond,
        VerifyCrc32CBytesPerSecond,
        VerifyReadBytes,
        VerifyCrc32CBytes);
    public double SourceReadWallClockBytesPerSecond => Rate(SourceReadBytes, CopyPhaseElapsed);
    public double FanoutLogicalWriteWallClockBytesPerSecond => Rate(WrittenBytes, CopyPhaseElapsed);
    public double SustainedWrite5sBytesPerSecond { get; init; }
    public double SustainedWrite10sBytesPerSecond { get; init; }
    public double SourceRead5sBytesPerSecond { get; init; }
    public double SourceRead10sBytesPerSecond { get; init; }
    public double VerifyLogical5sBytesPerSecond { get; init; }
    public double VerifyLogical10sBytesPerSecond { get; init; }
    public IReadOnlyList<DeviceIoSnapshot> DeviceSchedulers { get; init; } = [];
    public long DirectSourceReadBytes { get; init; }
    public long DirectSourceReadOperations { get; init; }
    public int DirectSourceFallbacks { get; init; }
    public int DirectDestinationFiles { get; init; }
    public long DirectDestinationWriteBytes { get; init; }
    public long DirectDestinationWriteOperations { get; init; }
    public int DirectDestinationFallbacks { get; init; }
    public int CurrentTransferBytes { get; init; }
    public int MinimumTransferBytes { get; init; }
    public int MaximumTransferBytes { get; init; }
    public IReadOnlyList<IoRecoveryEvent> RecentIoRecoveryEvents { get; init; } = [];
    public long SpillCopyBytes { get; init; }
    public TimeSpan SpillCopyTime { get; init; }
    public long PeakGlobalSpillBytes { get; init; }
    public long GlobalSpillCapacityBytes { get; init; }
    public string PreallocationPolicy { get; init; } = "unknown";
    public int DestinationDetaches { get; init; }
    public int LastDetachedSlot { get; init; } = -1;
    public long LastDetachOffset { get; init; } = -1;
    public double SpillCopyBytesPerSecond => Rate(SpillCopyBytes, SpillCopyTime);

    private static double Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes / elapsed.TotalSeconds;

    private static VerificationBottleneckKind ClassifyVerificationBottleneck(
        double readBytesPerSecond,
        double crc32CBytesPerSecond,
        long readBytes,
        long crc32CBytes)
    {
        if (readBytes <= 0 || crc32CBytes <= 0 || readBytesPerSecond <= 0 || crc32CBytesPerSecond <= 0)
            return VerificationBottleneckKind.None;

        const double materialDifference = 0.85;
        if (readBytesPerSecond < crc32CBytesPerSecond * materialDifference)
            return VerificationBottleneckKind.StorageRead;
        if (crc32CBytesPerSecond < readBytesPerSecond * materialDifference)
            return VerificationBottleneckKind.Crc32C;
        return VerificationBottleneckKind.Balanced;
    }
}

internal sealed class CopyTelemetry
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _preallocationPolicy = StoragePreallocationPolicy.DiagnosticState;
    private readonly SlidingByteRateWindow _writeRate = new();
    private readonly SlidingByteRateWindow _sourceReadRate = new();
    private readonly SlidingByteRateWindow _verifyLogicalRate = new();
    private readonly ConcurrentQueue<IoRecoveryEvent> _ioRecoveryEvents = new();
    private IReadOnlyCollection<DeviceScheduler>? _deviceSchedulers;
    private FanoutSpillBudget? _spillBudget;
    private long _sourceReadBytes, _sourceReadTicks;
    private long _directSourceReadBytes, _directSourceReadOperations;
    private int _directSourceFallbacks;
    private int _directDestinationFiles, _directDestinationFallbacks;
    private long _directDestinationWriteBytes, _directDestinationWriteOperations;
    private long _sourceHashBytes, _sourceHashTicks;
    private long _bufferWaitTicks;
    private long _writtenBytes, _writeOperations, _writeTicks;
    private int _flushes, _commits, _recoveryEvents;
    private long _flushTicks, _commitTicks, _recoveryTicks;
    private long _verifyReadBytes, _verifyReadTicks;
    private long _verifyCrc32CBytes, _verifyCrc32CTicks;
    private int _currentTransferBytes, _minimumTransferBytes = int.MaxValue, _maximumTransferBytes;
    private long _peakBufferedBytes, _maxObservedBufferTargetBytes;
    private long _copyPhaseTicks, _verifyPhaseTicks;
    private long _spillCopyBytes, _spillCopyTicks;
    private int _destinationDetaches, _lastDetachedSlot = -1;
    private long _lastDetachOffset = -1;

    internal void AttachDeviceSchedulers(IReadOnlyCollection<DeviceScheduler> schedulers) =>
        _deviceSchedulers = schedulers.ToArray();

    internal void AttachSpillBudget(FanoutSpillBudget budget) =>
        _spillBudget = budget ?? throw new ArgumentNullException(nameof(budget));

    internal void RecordDestinationDetached(int slot, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Interlocked.Exchange(ref _lastDetachOffset, offset);
        Volatile.Write(ref _lastDetachedSlot, slot);
        Interlocked.Increment(ref _destinationDetaches);
    }

    internal void RecordSpillCopy(int bytes, TimeSpan elapsed)
    {
        AddBytes(ref _spillCopyBytes, bytes);
        AddTicks(ref _spillCopyTicks, elapsed);
    }


    internal void RecordSourceRead(int bytes, TimeSpan elapsed)
    {
        AddBytes(ref _sourceReadBytes, bytes);
        AddTicks(ref _sourceReadTicks, elapsed);
        if (bytes > 0) _sourceReadRate.Record(bytes);
    }
    internal void RecordDirectSourceRead(int bytes) { AddBytes(ref _directSourceReadBytes, bytes); Interlocked.Increment(ref _directSourceReadOperations); }
    internal void RecordDirectSourceFallback() => Interlocked.Increment(ref _directSourceFallbacks);
    internal void RecordDirectDestinationFile() => Interlocked.Increment(ref _directDestinationFiles);
    internal void RecordDirectDestinationWrite(int bytes, int operations)
    {
        AddBytes(ref _directDestinationWriteBytes, bytes);
        if (operations > 0) Interlocked.Add(ref _directDestinationWriteOperations, operations);
    }
    internal void RecordDirectDestinationFallback() => Interlocked.Increment(ref _directDestinationFallbacks);
    internal void RecordIoRecovery(
        string phase,
        string path,
        string mode,
        int nativeErrorCode,
        int queueDepth,
        int retryCount,
        long offset,
        bool recovered)
    {
        _ioRecoveryEvents.Enqueue(new IoRecoveryEvent(
            DateTimeOffset.UtcNow,
            phase,
            path,
            mode,
            nativeErrorCode,
            Math.Max(1, queueDepth),
            Math.Max(0, retryCount),
            offset,
            recovered));
        while (_ioRecoveryEvents.Count > 128 && _ioRecoveryEvents.TryDequeue(out _)) { }
    }
    internal void RecordSourceHash(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceHashBytes, bytes); AddTicks(ref _sourceHashTicks, elapsed); }
    internal void RecordBufferWait(TimeSpan elapsed) => AddTicks(ref _bufferWaitTicks, elapsed);

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
    internal void RecordVerifyLogicalBytes(int bytes) { if (bytes > 0) _verifyLogicalRate.Record(bytes); }
    internal void RecordVerifyCrc32C(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyCrc32CBytes, bytes); AddTicks(ref _verifyCrc32CTicks, elapsed); }
    internal void RecordTransferSize(int bytes)
    {
        if (bytes <= 0) return;
        Volatile.Write(ref _currentTransferBytes, bytes);
        UpdateMin(ref _minimumTransferBytes, bytes);
        UpdateMax(ref _maximumTransferBytes, bytes);
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
        var sourceSustained = _sourceReadRate.Snapshot();
        var verifySustained = _verifyLogicalRate.Snapshot();

        var devices = _deviceSchedulers?
            .Select(item => item.Snapshot())
            .OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        return new CopyDiagnosticsSnapshot(
            Interlocked.Read(ref _sourceReadBytes), ToTimeSpan(Interlocked.Read(ref _sourceReadTicks)),
            Interlocked.Read(ref _sourceHashBytes), ToTimeSpan(Interlocked.Read(ref _sourceHashTicks)),
            ToTimeSpan(Interlocked.Read(ref _bufferWaitTicks)),
            Interlocked.Read(ref _writtenBytes), Interlocked.Read(ref _writeOperations), ToTimeSpan(Interlocked.Read(ref _writeTicks)),
            Volatile.Read(ref _flushes), ToTimeSpan(Interlocked.Read(ref _flushTicks)),
            Volatile.Read(ref _commits), ToTimeSpan(Interlocked.Read(ref _commitTicks)),
            Volatile.Read(ref _recoveryEvents), ToTimeSpan(Interlocked.Read(ref _recoveryTicks)),
            Interlocked.Read(ref _verifyReadBytes), ToTimeSpan(Interlocked.Read(ref _verifyReadTicks)),
            Interlocked.Read(ref _verifyCrc32CBytes), ToTimeSpan(Interlocked.Read(ref _verifyCrc32CTicks)),
            Interlocked.Read(ref _peakBufferedBytes), Interlocked.Read(ref _maxObservedBufferTargetBytes),
            ToTimeSpan(Interlocked.Read(ref _copyPhaseTicks)),
            ToTimeSpan(Interlocked.Read(ref _verifyPhaseTicks)),
            Stopwatch.GetElapsedTime(_started))
        {
            SustainedWrite5sBytesPerSecond = sustained.FiveSecondsBytesPerSecond,
            SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,
            SourceRead5sBytesPerSecond = sourceSustained.FiveSecondsBytesPerSecond,
            SourceRead10sBytesPerSecond = sourceSustained.TenSecondsBytesPerSecond,
            VerifyLogical5sBytesPerSecond = verifySustained.FiveSecondsBytesPerSecond,
            VerifyLogical10sBytesPerSecond = verifySustained.TenSecondsBytesPerSecond,
            DeviceSchedulers = devices,
            DirectSourceReadBytes = Interlocked.Read(ref _directSourceReadBytes),
            DirectSourceReadOperations = Interlocked.Read(ref _directSourceReadOperations),
            DirectSourceFallbacks = Volatile.Read(ref _directSourceFallbacks),
            DirectDestinationFiles = Volatile.Read(ref _directDestinationFiles),
            DirectDestinationWriteBytes = Interlocked.Read(ref _directDestinationWriteBytes),
            DirectDestinationWriteOperations = Interlocked.Read(ref _directDestinationWriteOperations),
            DirectDestinationFallbacks = Volatile.Read(ref _directDestinationFallbacks),
            CurrentTransferBytes = Volatile.Read(ref _currentTransferBytes),
            MinimumTransferBytes = Volatile.Read(ref _minimumTransferBytes) == int.MaxValue ? 0 : Volatile.Read(ref _minimumTransferBytes),
            MaximumTransferBytes = Volatile.Read(ref _maximumTransferBytes),
            RecentIoRecoveryEvents = _ioRecoveryEvents.ToArray(),
            SpillCopyBytes = Interlocked.Read(ref _spillCopyBytes),
            SpillCopyTime = ToTimeSpan(Interlocked.Read(ref _spillCopyTicks)),
            PeakGlobalSpillBytes = _spillBudget?.PeakUsedBytes ?? 0,
            GlobalSpillCapacityBytes = _spillBudget?.CapacityBytes ?? 0,
            PreallocationPolicy = _preallocationPolicy,
            DestinationDetaches = Volatile.Read(ref _destinationDetaches),
            LastDetachedSlot = Volatile.Read(ref _lastDetachedSlot),
            LastDetachOffset = Interlocked.Read(ref _lastDetachOffset),
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
    private static void UpdateMin(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value < current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
    private static void UpdateMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

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
