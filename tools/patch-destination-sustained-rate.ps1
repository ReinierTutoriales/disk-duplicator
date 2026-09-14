$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce([string]$text, [string]$old, [string]$new, [string]$label) {
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -ne 1) { throw "$label expected exactly one match, found $count." }
    return $text.Replace($old, $new)
}

$telemetryPath = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$modelsPath = 'dotnet/RepartoCopier.Core/Models.cs'
$telemetry = Get-Content -Raw $telemetryPath
$models = Get-Content -Raw $modelsPath

$window = @'
using System.Diagnostics;

namespace RepartoCopier.Core;

/// <summary>
/// Thread-safe sliding throughput window. Stores cumulative byte samples and
/// derives rates from actual recent progress, not an EWMA or instantaneous burst.
/// One implementation is shared by global diagnostics and each destination.
/// </summary>
internal sealed class SlidingByteRateWindow
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(12);
    private readonly object _gate = new();
    private readonly Queue<Sample> _samples = new();
    private long _totalBytes;

    internal void Record(int bytes) => Record(bytes, Stopwatch.GetTimestamp());

    internal void Record(int bytes, long timestamp)
    {
        if (bytes <= 0)
            return;
        lock (_gate)
        {
            _totalBytes = checked(_totalBytes + bytes);
            _samples.Enqueue(new Sample(timestamp, _totalBytes));
            TrimLocked(timestamp);
        }
    }

    internal SlidingByteRateSnapshot Snapshot() => Snapshot(Stopwatch.GetTimestamp());

    internal SlidingByteRateSnapshot Snapshot(long now)
    {
        lock (_gate)
        {
            TrimLocked(now);
            return new SlidingByteRateSnapshot(
                RateLocked(now, TimeSpan.FromSeconds(5)),
                RateLocked(now, TimeSpan.FromSeconds(10)));
        }
    }

    private double RateLocked(long now, TimeSpan window)
    {
        if (_totalBytes <= 0 || _samples.Count == 0)
            return 0;

        var cutoff = now - (long)(window.TotalSeconds * Stopwatch.Frequency);
        var baseline = _samples.Peek();
        foreach (var sample in _samples)
        {
            baseline = sample;
            if (sample.Timestamp >= cutoff)
                break;
        }

        var elapsed = Stopwatch.GetElapsedTime(Math.Max(baseline.Timestamp, cutoff), now).TotalSeconds;
        if (elapsed <= 0)
            return 0;
        var bytes = Math.Max(0L, _totalBytes - baseline.TotalBytes);
        return bytes / elapsed;
    }

    private void TrimLocked(long now)
    {
        var cutoff = now - (long)(Retention.TotalSeconds * Stopwatch.Frequency);
        while (_samples.Count > 1 && _samples.Peek().Timestamp < cutoff)
            _samples.Dequeue();
    }

    private readonly record struct Sample(long Timestamp, long TotalBytes);
}

internal readonly record struct SlidingByteRateSnapshot(
    double FiveSecondsBytesPerSecond,
    double TenSecondsBytesPerSecond);
'@
Set-Content 'dotnet/RepartoCopier.Core/SlidingByteRateWindow.cs' $window -NoNewline

# Global CopyTelemetry: replace its private duplicate sliding-window implementation.
$telemetry = Replace-ExactlyOnce $telemetry @'
    private readonly object _rateGate = new();
    private readonly Queue<WriteRateSample> _writeSamples = new();
'@ @'
    private readonly SlidingByteRateWindow _writeRate = new();
'@ 'global rate fields'

$telemetry = Replace-ExactlyOnce $telemetry @'
        if (bytes <= 0) return;
        var total = Interlocked.Read(ref _writtenBytes);
        var now = Stopwatch.GetTimestamp();
        lock (_rateGate)
        {
            _writeSamples.Enqueue(new WriteRateSample(now, total));
            TrimSamplesLocked(now, TimeSpan.FromSeconds(12));
        }
'@ @'
        _writeRate.Record(bytes);
'@ 'global record write rate'

$telemetry = Replace-ExactlyOnce $telemetry @'
        var now = Stopwatch.GetTimestamp();
        double sustained5;
        double sustained10;
        lock (_rateGate)
        {
            TrimSamplesLocked(now, TimeSpan.FromSeconds(12));
            sustained5 = RateFromSamplesLocked(now, TimeSpan.FromSeconds(5));
            sustained10 = RateFromSamplesLocked(now, TimeSpan.FromSeconds(10));
        }
'@ @'
        var sustained = _writeRate.Snapshot();
'@ 'global snapshot rate calculation'

$telemetry = $telemetry.Replace('SustainedWrite5sBytesPerSecond = sustained5,', 'SustainedWrite5sBytesPerSecond = sustained.FiveSecondsBytesPerSecond,')
$telemetry = $telemetry.Replace('SustainedWrite10sBytesPerSecond = sustained10,', 'SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,')

$start = $telemetry.IndexOf('    private double RateFromSamplesLocked(')
$end = $telemetry.IndexOf('    private static void AddBytes(', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Could not locate legacy global sliding-rate helpers.' }
$telemetry = $telemetry.Remove($start, $end - $start)

if ($telemetry -match '_rateGate|_writeSamples|WriteRateSample|RateFromSamplesLocked|TrimSamplesLocked') {
    throw 'Legacy global sliding-rate implementation remains.'
}
Set-Content -Path $telemetryPath -Value $telemetry -NoNewline

# DestinationSnapshot gets sustained values without changing its positional constructor surface.
$models = Replace-ExactlyOnce $models @'
{
    public DestinationSnapshot(
'@ @'
{
    public double SustainedWrite5sBytesPerSecond { get; init; }
    public double SustainedWrite10sBytesPerSecond { get; init; }

    public DestinationSnapshot(
'@ 'destination snapshot sustained properties'

$models = Replace-ExactlyOnce $models @'
    private double _writeEwma;
    private ulong _verifiedBytes;
'@ @'
    private double _writeEwma;
    private readonly SlidingByteRateWindow _sustainedWriteRate = new();
    private ulong _verifiedBytes;
'@ 'destination sustained rate field'

$models = Replace-ExactlyOnce $models @'
            Written += (ulong)bytes;
            RecordWriteSampleLocked((ulong)bytes, Stopwatch.GetTimestamp());
'@ @'
            Written += (ulong)bytes;
            var now = Stopwatch.GetTimestamp();
            RecordWriteSampleLocked((ulong)bytes, now);
            _sustainedWriteRate.Record(bytes, now);
'@ 'destination final write recording'

$models = Replace-ExactlyOnce $models @'
            var displayedBps = DisplayedWriteBpsLocked(nowTick);
            return new DestinationSnapshot(
'@ @'
            var displayedBps = DisplayedWriteBpsLocked(nowTick);
            var sustained = _sustainedWriteRate.Snapshot(nowTick);
            return new DestinationSnapshot(
'@ 'destination snapshot rate read'

$models = Replace-ExactlyOnce $models @'
                LastFile,
                QueueDepth,
                Retries);
'@ @'
                LastFile,
                QueueDepth,
                Retries)
            {
                SustainedWrite5sBytesPerSecond = sustained.FiveSecondsBytesPerSecond,
                SustainedWrite10sBytesPerSecond = sustained.TenSecondsBytesPerSecond,
            };
'@ 'destination snapshot sustained wiring'

$models = Replace-ExactlyOnce $models @'
    private void RecordWriteSampleLocked(ulong bytes, long nowTick)
'@ @'
    internal SlidingByteRateSnapshot SustainedWriteRateSnapshot() =>
        _sustainedWriteRate.Snapshot();

    internal SlidingByteRateSnapshot SustainedWriteRateSnapshot(long nowTick) =>
        _sustainedWriteRate.Snapshot(nowTick);

    private void RecordWriteSampleLocked(ulong bytes, long nowTick)
'@ 'destination internal spill signal'

Set-Content -Path $modelsPath -Value $models -NoNewline

$tests = @'
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SlidingByteRateWindowTests
{
    [TestMethod]
    public void FiveAndTenSecondRatesUseRealSlidingSamples()
    {
        var window = new SlidingByteRateWindow();
        var origin = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;

        window.Record(100, origin);
        window.Record(100, origin + 2 * second);
        window.Record(100, origin + 4 * second);
        window.Record(100, origin + 6 * second);
        window.Record(100, origin + 8 * second);
        window.Record(100, origin + 10 * second);

        var snapshot = window.Snapshot(origin + 10 * second);

        Assert.AreEqual(50.0, snapshot.FiveSecondsBytesPerSecond, 0.001);
        Assert.AreEqual(50.0, snapshot.TenSecondsBytesPerSecond, 0.001);
    }

    [TestMethod]
    public void OldProgressDecaysToZeroWithoutAnEwmaTail()
    {
        var window = new SlidingByteRateWindow();
        var origin = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;

        window.Record(1024, origin);
        window.Record(1024, origin + second);

        var snapshot = window.Snapshot(origin + 20 * second);

        Assert.AreEqual(0.0, snapshot.FiveSecondsBytesPerSecond);
        Assert.AreEqual(0.0, snapshot.TenSecondsBytesPerSecond);
    }

    [TestMethod]
    public void DestinationSnapshotCarriesSustainedRatesSeparateFromUiEwma()
    {
        var progress = new DestinationProgress("dest", 4096);
        var origin = Stopwatch.GetTimestamp();
        var second = Stopwatch.Frequency;
        progress.SetPhase(DestinationPhase.Copying);

        progress.AddWritten(1024);
        var rates = progress.SustainedWriteRateSnapshot(origin + second);

        Assert.IsTrue(rates.FiveSecondsBytesPerSecond >= 0);
        var snapshot = progress.Snapshot(origin + second);
        Assert.IsTrue(snapshot.SustainedWrite5sBytesPerSecond >= 0);
        Assert.IsTrue(snapshot.SustainedWrite10sBytesPerSecond >= 0);
    }
}
'@
Set-Content 'dotnet/RepartoCopier.Core.Tests/SlidingByteRateWindowTests.cs' $tests -NoNewline

$architecture = @'
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class SustainedDestinationRateArchitectureTests
{
    [TestMethod]
    public void GlobalAndPerDestinationRatesShareOneSlidingWindowPrimitive()
    {
        var telemetryFieldTypes = typeof(CopyTelemetry)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        CollectionAssert.Contains(telemetryFieldTypes, typeof(SlidingByteRateWindow));

        var progressFieldTypes = typeof(DestinationProgress)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();
        CollectionAssert.Contains(progressFieldTypes, typeof(SlidingByteRateWindow));

        var snapshotProperties = typeof(DestinationSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.Contains(snapshotProperties, nameof(DestinationSnapshot.SustainedWrite5sBytesPerSecond));
        CollectionAssert.Contains(snapshotProperties, nameof(DestinationSnapshot.SustainedWrite10sBytesPerSecond));
    }
}
'@
Set-Content 'dotnet/RepartoCopier.Core.Tests/SustainedDestinationRateArchitectureTests.cs' $architecture -NoNewline
