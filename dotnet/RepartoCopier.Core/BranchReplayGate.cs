using System.Diagnostics;

namespace RepartoCopier.Core;

/// <summary>
/// Per-destination replay state. Entry and exit require sustained observations
/// and use different watermarks so a transient backlog spike cannot flap replay.
/// </summary>
internal sealed class BranchReplayGate
{
    internal static readonly TimeSpan DefaultEnterDuration = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan DefaultExitDuration = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _enterDuration;
    private readonly TimeSpan _exitDuration;
    private long _highSince;
    private long _lowSince;
    private bool _active;

    internal BranchReplayGate()
        : this(DefaultEnterDuration, DefaultExitDuration)
    {
    }

    internal BranchReplayGate(TimeSpan enterDuration, TimeSpan exitDuration)
    {
        if (enterDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(enterDuration));
        if (exitDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exitDuration));
        _enterDuration = enterDuration;
        _exitDuration = exitDuration;
    }

    internal bool IsActive => _active;

    internal bool ShouldReplay(long pendingBytes, long backlogTargetBytes, long timestamp)
    {
        if (backlogTargetBytes <= 0)
            return false;

        var enterThreshold = backlogTargetBytes;
        var exitThreshold = Math.Max(0L, backlogTargetBytes / 2);

        if (!_active)
        {
            _lowSince = 0;
            if (pendingBytes < enterThreshold)
            {
                _highSince = 0;
                return false;
            }

            if (_highSince == 0)
            {
                _highSince = timestamp;
                return _enterDuration == TimeSpan.Zero && Activate();
            }

            if (Stopwatch.GetElapsedTime(_highSince, timestamp) < _enterDuration)
                return false;

            return Activate();
        }

        _highSince = 0;
        if (pendingBytes > exitThreshold)
        {
            _lowSince = 0;
            return true;
        }

        if (_lowSince == 0)
        {
            _lowSince = timestamp;
            if (_exitDuration != TimeSpan.Zero)
                return true;
        }
        else if (Stopwatch.GetElapsedTime(_lowSince, timestamp) < _exitDuration)
        {
            return true;
        }

        _active = false;
        _lowSince = 0;
        return false;
    }

    private bool Activate()
    {
        _active = true;
        _highSince = 0;
        _lowSince = 0;
        return true;
    }
}