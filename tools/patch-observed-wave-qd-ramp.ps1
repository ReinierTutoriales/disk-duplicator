$ErrorActionPreference = 'Stop'

$schedulerPath = 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
$testsPath = 'dotnet/RepartoCopier.Core.Tests/DeviceSchedulerTests.cs'
$scheduler = Get-Content -Raw $schedulerPath
$tests = Get-Content -Raw $testsPath

function Replace-ExactlyOnce([string]$text, [string]$old, [string]$new, [string]$label) {
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -ne 1) { throw "$label expected exactly one match, found $count." }
    return $text.Replace($old, $new)
}

$scheduler = Replace-ExactlyOnce $scheduler `
'    private int _sampleCompletions;
    private bool _sampleSawDemand;' `
'    private int _sampleCompletions;
    private int _samplePeakObservedConcurrency;
    private bool _sampleSawDemand;' `
'sample peak field'

$scheduler = Replace-ExactlyOnce $scheduler `
'        _sampleCompletions++;
        if (_ioWaiters.Count > 0 || _outstandingIo >= _currentQueueDepth)
            _sampleSawDemand = true;

        var decisionInterval = Math.Max(8, (int)Math.Min(256L, (long)_currentQueueDepth * 2));
        if (_sampleCompletions < decisionInterval)
            return;' `
'        _sampleCompletions++;
        // One completed operation plus the operations that are still outstanding
        // describe the concurrency this sample actually exercised. Requiring one
        // observed wave gives throughput/latency feedback without delaying QD
        // exploration behind an arbitrary completion-count floor or ceiling.
        _samplePeakObservedConcurrency = Math.Max(
            _samplePeakObservedConcurrency,
            checked(_outstandingIo + 1));
        if (_ioWaiters.Count > 0 || _outstandingIo >= _currentQueueDepth)
            _sampleSawDemand = true;

        var observedWaveCompletions = Math.Max(1, _samplePeakObservedConcurrency);
        if (_sampleCompletions < observedWaveCompletions)
            return;' `
'QD decision cadence'

$scheduler = Replace-ExactlyOnce $scheduler `
'        _sampleLatencyStopwatchTicks = 0;
        _sampleCompletions = 0;
        _sampleSawDemand = false;' `
'        _sampleLatencyStopwatchTicks = 0;
        _sampleCompletions = 0;
        _samplePeakObservedConcurrency = 0;
        _sampleSawDemand = false;' `
'sample reset'

if ($scheduler -match 'decisionInterval|Math\.Max\(8, \(int\)Math\.Min\(256L') {
    throw 'Legacy fixed completion cadence remains in DeviceScheduler.'
}

$anchor = @'
    [TestMethod]
    public async Task SustainedDemandCanGrowBeyondLegacyNvmeQd16()
'@
$test = @'
    [TestMethod]
    public async Task QdOneDemandUpshiftsAfterOneObservedConcurrencyWave()
    {
        using var scheduler = new DeviceScheduler("PhysicalDiskFastStart", 1, 32L * 1024 * 1024);
        var first = await scheduler.AcquireIoAsync(256 * 1024, CancellationToken.None);
        var waiting = scheduler.AcquireIoAsync(256 * 1024, CancellationToken.None).AsTask();

        Assert.AreEqual(1, scheduler.CurrentQueueDepth);
        Assert.IsFalse(waiting.IsCompleted);

        first.Dispose();

        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, scheduler.CurrentQueueDepth,
            "Sustained QD1 demand should be evaluated after the single concurrency wave actually observed, not after a fixed eight-completion floor.");
        Assert.AreEqual("increase:baseline-demand", scheduler.Snapshot().LastQueueDepthDecision);
    }

'@
if ($tests -notmatch 'QdOneDemandUpshiftsAfterOneObservedConcurrencyWave') {
    $tests = Replace-ExactlyOnce $tests $anchor ($test + $anchor) 'fast QD ramp test anchor'
}

# The scheduler may legitimately continue exploring farther now that feedback
# arrives sooner; the old exact-QD32 assertion encoded the slower cadence.
$tests = Replace-ExactlyOnce $tests `
'        Assert.AreEqual(32, scheduler.CurrentQueueDepth);
        Assert.AreEqual(64, scheduler.ExplorationQueueDepth);' `
'        Assert.IsGreaterThanOrEqualTo(32, scheduler.CurrentQueueDepth);
        Assert.IsGreaterThan(scheduler.CurrentQueueDepth, scheduler.ExplorationQueueDepth);' `
'adaptive NVMe expectation'

Set-Content -Path $schedulerPath -Value $scheduler -NoNewline
Set-Content -Path $testsPath -Value $tests -NoNewline
