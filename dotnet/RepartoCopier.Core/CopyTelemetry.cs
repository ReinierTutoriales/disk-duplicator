using System.Diagnostics;
using System.Threading;

namespace RepartoCopier.Core;

public sealed record CopyDiagnosticsSnapshot(
    long SourceReadBytes,
    TimeSpan SourceReadTime,
    long SourceHashBytes,
    TimeSpan SourceHashTime,
    TimeSpan BufferWaitTime,
    TimeSpan FanoutWaitTime,
    TimeSpan QueueWaitTime,
    TimeSpan ControlBacklogWaitTime,
    long WrittenBytes,
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
    TimeSpan VerifyCpuWaitTime,
    int PeakControlBacklogMessages,
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

    private static double Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes / elapsed.TotalSeconds;
}

internal sealed class CopyTelemetry
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _sourceReadBytes, _sourceReadTicks;
    private long _sourceHashBytes, _sourceHashTicks;
    private long _bufferWaitTicks, _fanoutWaitTicks, _queueWaitTicks, _controlBacklogWaitTicks;
    private long _writtenBytes, _writeTicks;
    private int _flushes, _commits, _recoveryEvents;
    private long _flushTicks, _commitTicks, _recoveryTicks;
    private long _verifyReadBytes, _verifyReadTicks;
    private long _verifyHashBytes, _verifyHashTicks, _verifyCpuWaitTicks;
    private int _peakControlBacklogMessages;
    private long _peakBufferedBytes, _maxObservedBufferTargetBytes;
    private long _copyPhaseTicks, _verifyPhaseTicks;

    internal void RecordSourceRead(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceReadBytes, bytes); AddTicks(ref _sourceReadTicks, elapsed); }
    internal void RecordSourceHash(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceHashBytes, bytes); AddTicks(ref _sourceHashTicks, elapsed); }
    internal void RecordBufferWait(TimeSpan elapsed) => AddTicks(ref _bufferWaitTicks, elapsed);
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);
    internal void RecordQueueWait(TimeSpan elapsed) => AddTicks(ref _queueWaitTicks, elapsed);
    internal void RecordControlBacklogWait(TimeSpan elapsed) => AddTicks(ref _controlBacklogWaitTicks, elapsed);
    internal void RecordWrite(int bytes, TimeSpan elapsed) { AddBytes(ref _writtenBytes, bytes); AddTicks(ref _writeTicks, elapsed); }
    internal void RecordFlush(TimeSpan elapsed) { Interlocked.Increment(ref _flushes); AddTicks(ref _flushTicks, elapsed); }
    internal void RecordCommit(TimeSpan elapsed) { Interlocked.Increment(ref _commits); AddTicks(ref _commitTicks, elapsed); }
    internal void RecordRecovery(TimeSpan elapsed) { Interlocked.Increment(ref _recoveryEvents); AddTicks(ref _recoveryTicks, elapsed); }
    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }
    internal void RecordVerifyHash(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyHashBytes, bytes); AddTicks(ref _verifyHashTicks, elapsed); }
    internal void RecordVerifyCpuWait(TimeSpan elapsed) => AddTicks(ref _verifyCpuWaitTicks, elapsed);
    internal void RecordCopyPhase(TimeSpan elapsed) => AddTicks(ref _copyPhaseTicks, elapsed);
    internal void RecordVerifyPhase(TimeSpan elapsed) => AddTicks(ref _verifyPhaseTicks, elapsed);

    internal void ObserveControlBacklog(int usedMessages) =>
        UpdateMax(ref _peakControlBacklogMessages, usedMessages);

    internal void ObserveBuffer(long usedBytes, long targetBytes)
    {
        UpdateMax(ref _peakBufferedBytes, usedBytes);
        UpdateMax(ref _maxObservedBufferTargetBytes, targetBytes);
    }

    internal CopyDiagnosticsSnapshot Snapshot() => new(
        Interlocked.Read(ref _sourceReadBytes), ToTimeSpan(Interlocked.Read(ref _sourceReadTicks)),
        Interlocked.Read(ref _sourceHashBytes), ToTimeSpan(Interlocked.Read(ref _sourceHashTicks)),
        ToTimeSpan(Interlocked.Read(ref _bufferWaitTicks)),
        ToTimeSpan(Interlocked.Read(ref _fanoutWaitTicks)),
        ToTimeSpan(Interlocked.Read(ref _queueWaitTicks)),
        ToTimeSpan(Interlocked.Read(ref _controlBacklogWaitTicks)),
        Interlocked.Read(ref _writtenBytes), ToTimeSpan(Interlocked.Read(ref _writeTicks)),
        Volatile.Read(ref _flushes), ToTimeSpan(Interlocked.Read(ref _flushTicks)),
        Volatile.Read(ref _commits), ToTimeSpan(Interlocked.Read(ref _commitTicks)),
        Volatile.Read(ref _recoveryEvents), ToTimeSpan(Interlocked.Read(ref _recoveryTicks)),
        Interlocked.Read(ref _verifyReadBytes), ToTimeSpan(Interlocked.Read(ref _verifyReadTicks)),
        Interlocked.Read(ref _verifyHashBytes), ToTimeSpan(Interlocked.Read(ref _verifyHashTicks)),
        ToTimeSpan(Interlocked.Read(ref _verifyCpuWaitTicks)),
        Volatile.Read(ref _peakControlBacklogMessages),
        Interlocked.Read(ref _peakBufferedBytes), Interlocked.Read(ref _maxObservedBufferTargetBytes),
        ToTimeSpan(Interlocked.Read(ref _copyPhaseTicks)),
        ToTimeSpan(Interlocked.Read(ref _verifyPhaseTicks)),
        Stopwatch.GetElapsedTime(_started));

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
}
