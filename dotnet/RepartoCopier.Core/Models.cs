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
    bool Verify = true,
    bool SkipSame = true,
    bool KeepGoing = false);

public sealed record DestinationSnapshot(
    string Label,
    ulong Written,
    ulong Total,
    ulong FilesDone,
    ulong FilesSkipped,
    ulong FilesErrored,
    double BytesPerSecond,
    double RecentBytesPerSecond,
    DestinationPhase Phase,
    string? Error,
    string LastFile,
    int QueueDepth,
    ulong Retries);

internal sealed class DestinationProgress
{
    private readonly object _gate = new();
    private DateTime _lastTick = DateTime.UtcNow;
    private ulong _lastWritten;

    public DestinationProgress(string label, ulong total)
    {
        Label = label;
        Total = total;
    }

    public string Label { get; }
    public ulong Total { get; }
    public ulong Written { get; private set; }
    public ulong FilesDone { get; private set; }
    public ulong FilesSkipped { get; private set; }
    public ulong FilesErrored { get; private set; }
    public double BytesPerSecond { get; private set; }
    public double RecentBytesPerSecond { get; private set; }
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
        lock (_gate)
        {
            Written += (ulong)bytes;
            UpdateSpeedLocked();
        }
    }

    public void RollbackWritten(ulong bytes)
    {
        lock (_gate)
        {
            Written = Written >= bytes ? Written - bytes : 0;
            UpdateSpeedLocked();
        }
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
            UpdateSpeedLocked();
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

    public DestinationSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DestinationSnapshot(
                Label,
                Written,
                Total,
                FilesDone,
                FilesSkipped,
                FilesErrored,
                BytesPerSecond,
                RecentBytesPerSecond,
                Phase,
                Error,
                LastFile,
                QueueDepth,
                Retries);
        }
    }

    private void UpdateSpeedLocked()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        if (elapsed <= 0.05) return;
        var delta = Written >= _lastWritten ? Written - _lastWritten : 0;
        var instantaneous = delta / elapsed;
        var alpha = 1.0 - Math.Exp(-elapsed / 2.0);
        RecentBytesPerSecond = RecentBytesPerSecond == 0
            ? instantaneous
            : RecentBytesPerSecond + alpha * (instantaneous - RecentBytesPerSecond);
        BytesPerSecond = RecentBytesPerSecond;
        _lastWritten = Written;
        _lastTick = now;
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
