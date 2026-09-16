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
}

public sealed record CopyOptions(
    bool Verify = false,
    bool SkipSame = true,
    bool KeepGoing = false,
    bool EnableReplay = true);

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
    public double SustainedWrite5sBytesPerSecond { get; init; }
    public double SustainedWrite10sBytesPerSecond { get; init; }

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
    private ulong _verifiedBytes;
    private ulong _verifyBytesTotal;
    private ulong _verifyFilesDone;
    private ulong _verifyFilesTotal;

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
            _verifyFilesDone = Math.Min(_verifyFilesTotal, _verifyFilesDone + 1);
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
                SustainedWrite5sBytesPerSecond = sustained.FiveSecondsBytesPerSecond,
                SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,
            };
        }
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
