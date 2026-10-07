using System.Diagnostics;

namespace RepartoCopier.Core;

public enum DestinationPhase
{
    Idle,
    Copying,
    Verifying,
    Done,
    Failed,
    Cancelled,
    Comparing,
}

/// <summary>One phase transition of a destination, as an offset from the creation of its progress tracker.</summary>
public sealed record DestinationPhaseMark(DestinationPhase Phase, TimeSpan Offset);

public sealed record CopyOptions(
    bool Verify = false,
    bool KeepGoing = false,
    bool IndependentSourceReads = false);

public sealed record DestinationSnapshot(
    string Label,
    ulong Written,
    ulong Total,
    ulong FilesDone,
    ulong FilesTotal,
    ulong FilesSkipped,
    ulong FilesErrored,
    ulong VerifiedBytes,
    ulong VerifyBytesTotal,
    ulong VerifyFilesDone,
    ulong VerifyFilesTotal,
    double BytesPerSecond,
    double RecentBytesPerSecond,
    DestinationPhase Phase,
    string? Error,
    string LastFile,
    int QueueDepth,
    ulong Retries)
{
    public ulong ComparisonBytesRead { get; init; }
    public ulong ComparisonBytesTotal { get; init; }
    public ulong ComparisonBytesProcessed { get; init; }
    public ulong ComparisonFilesDone { get; init; }
    public ulong ComparisonFilesTotal { get; init; }
    public ulong ComparisonIdenticalFiles { get; init; }

    public double SustainedWrite5sBytesPerSecond { get; init; }
    public double SustainedWrite10sBytesPerSecond { get; init; }

    // Identification of the physical device that receives this destination.
    public string DeviceId { get; init; } = string.Empty;
    public uint? PhysicalDeviceNumber { get; init; }
    public string BusType { get; init; } = string.Empty;
    public StorageMediaKind MediaKind { get; init; }
    public bool? Removable { get; init; }
    public bool SharesPhysicalDevice { get; init; }

    // Accumulated duration of this destination's write calls (not wall time).
    public TimeSpan WriteTime { get; init; }
    public ulong DurableFlushes { get; init; }
    public TimeSpan DurableFlushTime { get; init; }

    // Offsets from the creation of this destination's progress tracker. A null value means
    // the event did not happen (it is never filled in for failed or cancelled work).
    public TimeSpan? CopyStartedAt { get; init; }
    public DateTimeOffset? TrackingStartedAt { get; init; }
    public TimeSpan? CopyFinishedAt { get; init; }
    public TimeSpan? VerifyStartedAt { get; init; }
    public TimeSpan? VerifyFinishedAt { get; init; }
    public IReadOnlyList<DestinationPhaseMark> PhaseMarks { get; init; } = [];

    public TimeSpan? CopyDuration =>
        CopyStartedAt is { } started && CopyFinishedAt is { } finished ? finished - started : null;

    public TimeSpan? VerifyDuration =>
        VerifyStartedAt is { } started && VerifyFinishedAt is { } finished ? finished - started : null;

    public string Outcome => Phase switch
    {
        DestinationPhase.Done => FilesErrored > 0 ? "CompletedWithErrors" : "Completed",
        DestinationPhase.Failed => "Failed",
        DestinationPhase.Cancelled => "Cancelled",
        _ => "InProgress",
    };

    public DestinationSnapshot(
        string label,
        ulong written,
        ulong total,
        ulong filesDone,
        ulong filesSkipped,
        ulong filesErrored,
        double bytesPerSecond,
        double recentBytesPerSecond,
        DestinationPhase phase,
        string? error,
        string lastFile,
        int queueDepth,
        ulong retries)
        : this(
            label,
            written,
            total,
            filesDone,
            filesDone,
            filesSkipped,
            filesErrored,
            0,
            0,
            0,
            0,
            bytesPerSecond,
            recentBytesPerSecond,
            phase,
            error,
            lastFile,
            queueDepth,
            retries)
    {
    }

    public double CopyFraction =>
        Total == 0 ? 1.0 : Math.Clamp((double)Written / Total, 0.0, 1.0);

    public double VerifyFraction =>
        VerifyBytesTotal == 0 ? 1.0 : Math.Clamp((double)VerifiedBytes / VerifyBytesTotal, 0.0, 1.0);
}

internal sealed class DestinationProgress
{
    private readonly object _gate = new();
    private long _writeSampleTick = Stopwatch.GetTimestamp();
    private long _writeLastEventTick = Stopwatch.GetTimestamp();
    private ulong _writeSampleBytes;
    private double _writeEwma;
    private readonly SlidingByteRateWindow _sustainedWriteRate = new();
    private ulong _comparisonRead;
    private ulong _comparisonTotal;
    private ulong _comparisonProcessed;
    private ulong _comparisonFileRead;
    private ulong _comparisonFilesDone;
    private ulong _comparisonFilesTotal;
    private ulong _comparisonIdenticalFiles;
    private ulong _verifiedBytes;
    private ulong _verifyBytesTotal;
    private ulong _verifyFilesDone;
    private ulong _verifyFilesTotal;
    private readonly DateTimeOffset _trackingStartedAt = DateTimeOffset.UtcNow;
    private readonly long _originTick = Stopwatch.GetTimestamp();
    private readonly List<DestinationPhaseMark> _phaseMarks = [];
    private TimeSpan _writeTime;
    private ulong _durableFlushes;
    private TimeSpan _durableFlushTime;
    private TimeSpan? _copyStartedAt;
    private TimeSpan? _copyFinishedAt;
    private TimeSpan? _verifyStartedAt;
    private TimeSpan? _verifyFinishedAt;
    private string _deviceId = string.Empty;
    private StorageDeviceInfo? _device;

    public DestinationProgress(string label, ulong total, ulong filesTotal = 0)
    {
        Label = label;
        Total = total;
        FilesTotal = filesTotal;
    }

    public string Label { get; }
    public ulong Total { get; }
    public ulong Written { get; private set; }
    public ulong FilesDone { get; private set; }
    public ulong FilesTotal { get; }
    public ulong FilesSkipped { get; private set; }
    public ulong FilesErrored { get; private set; }
    public DestinationPhase Phase { get; private set; } = DestinationPhase.Idle;
    public string? Error { get; private set; }
    public string LastFile { get; private set; } = string.Empty;
    public int QueueDepth { get; private set; }
    public ulong Retries { get; private set; }

    public void SetPhase(DestinationPhase phase, string? error = null)
    {
        lock (_gate)
        {
            Phase = phase;
            if (error is not null) Error = error;
            RecordPhaseMarkLocked(phase);
        }
    }

    internal void SetDevice(string deviceId, StorageDeviceInfo device)
    {
        lock (_gate)
        {
            _deviceId = deviceId;
            _device = device;
        }
    }

    // Accumulates the duration of one write call of this destination. Instrumentation only.
    internal void AddWriteTime(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        lock (_gate) _writeTime += elapsed;
    }

    internal void AddDurableFlush(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _durableFlushes++;
            _durableFlushTime += elapsed;
        }
    }

    // Marks the end of this destination's COPY work. It is recorded only when the destination
    // is still copying and every file was either finished, skipped or reported as an error, so
    // a failed, cancelled or interrupted destination never receives a completion mark.
    internal void MarkCopyFinished()
    {
        lock (_gate)
        {
            if (Phase is not DestinationPhase.Copying || _copyFinishedAt is not null) return;
            if (FilesDone + FilesErrored < FilesTotal) return;
            _copyFinishedAt = ElapsedLocked();
        }
    }

    public void SetLastFile(string path)
    {
        lock (_gate) LastFile = path;
    }

    public void SetQueueDepth(int value)
    {
        lock (_gate) QueueDepth = Math.Max(0, value);
    }

    public void AddRetry()
    {
        lock (_gate) Retries++;
    }

    public void AddWritten(int bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            Written += (ulong)bytes;
            var now = Stopwatch.GetTimestamp();
            RecordWriteSampleLocked((ulong)bytes, now);
            _sustainedWriteRate.Record(bytes, now);
        }
    }

    public void RollbackWritten(ulong bytes)
    {
        lock (_gate)
            Written = Written >= bytes ? Written - bytes : 0;
    }

    public void MarkDone()
    {
        lock (_gate) FilesDone++;
    }

    public void MarkSkipped(ulong bytes)
    {
        lock (_gate)
        {
            FilesSkipped++;
            FilesDone++;
            Written += bytes;
        }
    }

    public void MarkError(string error)
    {
        lock (_gate)
        {
            FilesErrored++;
            Error = error;
        }
    }

    internal void BeginComparison(ulong bytes, ulong files = 0)
    {
        lock (_gate)
        {
            _comparisonRead = _comparisonProcessed = _comparisonFileRead = 0;
            _comparisonFilesDone = _comparisonIdenticalFiles = 0;
            _comparisonTotal = bytes;
            _comparisonFilesTotal = files;
            Phase = DestinationPhase.Comparing;
            RecordPhaseMarkLocked(Phase);
        }
    }

    internal void AddCompared(int bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
        {
            _comparisonRead = checked(_comparisonRead + (ulong)bytes);
            _comparisonFileRead = checked(_comparisonFileRead + (ulong)bytes);
        }
    }

    internal void MarkComparisonFileDone(ulong size, bool identical)
    {
        lock (_gate)
        {
            _comparisonProcessed = checked(_comparisonProcessed + size);
            _comparisonFileRead = 0;
            _comparisonFilesDone++;
            if (identical) _comparisonIdenticalFiles++;
        }
    }

    public void SetVerifyWork(ulong bytes, ulong files)
    {
        lock (_gate)
        {
            _verifiedBytes = 0;
            _verifyBytesTotal = bytes;
            _verifyFilesDone = 0;
            _verifyFilesTotal = files;
        }
    }

    public void AddVerified(int bytes)
    {
        if (bytes <= 0) return;
        lock (_gate)
            _verifiedBytes = Math.Min(_verifyBytesTotal, checked(_verifiedBytes + (ulong)bytes));
    }

    public void MarkVerifyFileDone()
    {
        lock (_gate)
        {
            _verifyFilesDone = Math.Min(_verifyFilesTotal, _verifyFilesDone + 1);
            if (Phase is DestinationPhase.Verifying && _verifyFinishedAt is null && _verifyFilesDone >= _verifyFilesTotal)
                _verifyFinishedAt = ElapsedLocked();
        }
    }

    public DestinationSnapshot Snapshot() => Snapshot(Stopwatch.GetTimestamp());

    internal DestinationSnapshot Snapshot(long nowTick)
    {
        lock (_gate)
        {
            var displayedBps = DisplayedWriteBpsLocked(nowTick);
            var sustained = _sustainedWriteRate.Snapshot(nowTick);
            return new DestinationSnapshot(
                Label,
                Written,
                Total,
                FilesDone,
                FilesTotal,
                FilesSkipped,
                FilesErrored,
                _verifiedBytes,
                _verifyBytesTotal,
                _verifyFilesDone,
                _verifyFilesTotal,
                displayedBps,
                displayedBps,
                Phase,
                Error,
                LastFile,
                QueueDepth,
                Retries)
            {
                ComparisonBytesRead = _comparisonRead,
                ComparisonBytesTotal = _comparisonTotal,
                ComparisonBytesProcessed = Math.Min(_comparisonTotal, checked(_comparisonProcessed + _comparisonFileRead)),
                ComparisonFilesDone = _comparisonFilesDone,
                ComparisonFilesTotal = _comparisonFilesTotal,
                ComparisonIdenticalFiles = _comparisonIdenticalFiles,
                SustainedWrite5sBytesPerSecond = sustained.FiveSecondsBytesPerSecond,
                SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,
                DeviceId = _deviceId,
                PhysicalDeviceNumber = _device?.PhysicalDeviceNumber,
                BusType = _device?.BusType ?? string.Empty,
                MediaKind = _device?.MediaKind ?? StorageMediaKind.Unknown,
                Removable = _device?.Removable,
                SharesPhysicalDevice = _device?.SharesPhysicalDevice ?? false,
                WriteTime = _writeTime,
                DurableFlushes = _durableFlushes,
                DurableFlushTime = _durableFlushTime,
                CopyStartedAt = _copyStartedAt,
                TrackingStartedAt = _trackingStartedAt,
                CopyFinishedAt = _copyFinishedAt,
                VerifyStartedAt = _verifyStartedAt,
                VerifyFinishedAt = _verifyFinishedAt,
                PhaseMarks = _phaseMarks.ToArray(),
            };
        }
    }

    private TimeSpan ElapsedLocked() => Stopwatch.GetElapsedTime(_originTick);

    private void RecordPhaseMarkLocked(DestinationPhase phase)
    {
        if (_phaseMarks.Count > 0 && _phaseMarks[^1].Phase == phase) return;
        var offset = ElapsedLocked();
        _phaseMarks.Add(new DestinationPhaseMark(phase, offset));
        if (phase is DestinationPhase.Copying) _copyStartedAt ??= offset;
        if (phase is DestinationPhase.Verifying) _verifyStartedAt ??= offset;
    }

    internal SlidingByteRateSnapshot SustainedWriteRateSnapshot() =>
        _sustainedWriteRate.Snapshot();

    internal SlidingByteRateSnapshot SustainedWriteRateSnapshot(long nowTick) =>
        _sustainedWriteRate.Snapshot(nowTick);

    private void RecordWriteSampleLocked(ulong bytes, long nowTick)
    {
        _writeLastEventTick = nowTick;
        _writeSampleBytes = checked(_writeSampleBytes + bytes);
        var elapsed = Stopwatch.GetElapsedTime(_writeSampleTick, nowTick).TotalSeconds;
        if (elapsed <= 0.05)
            return;

        var instantaneous = _writeSampleBytes / elapsed;
        var alpha = 1.0 - Math.Exp(-elapsed / 2.0);
        _writeEwma = _writeEwma == 0
            ? instantaneous
            : _writeEwma + alpha * (instantaneous - _writeEwma);
        _writeSampleBytes = 0;
        _writeSampleTick = nowTick;
    }

    private double DisplayedWriteBpsLocked(long nowTick)
    {
        if (Phase is not DestinationPhase.Copying || _writeEwma <= 0)
            return 0;

        var idleSeconds = Stopwatch.GetElapsedTime(_writeLastEventTick, nowTick).TotalSeconds;
        if (idleSeconds <= 0.5)
            return _writeEwma;
        return _writeEwma * Math.Exp(-(idleSeconds - 0.5) / 2.0);
    }
}

public static class Throughput
{
    public static string Format(double bytesPerSecond)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var index = 0;
        while (value >= 1024.0 && index < units.Length - 1)
        {
            value /= 1024.0;
            index++;
        }
        return $"{value:0.0} {units[index]}";
    }
}
