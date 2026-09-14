$ErrorActionPreference = 'Stop'

$schedulerPath = 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
$testsPath = 'dotnet/RepartoCopier.Core.Tests/DeviceSchedulerTests.cs'

$scheduler = Get-Content $schedulerPath -Raw
$old = @'
        var decisionInterval = Math.Max(8, (int)Math.Min(256L, (long)_currentQueueDepth * 2));
        if (_sampleCompletions < decisionInterval)
            return;
'@
$new = @'
        // One saturated physical queue window is enough evidence to evaluate
        // whether the current depth should keep expanding. The previous
        // max(8, min(256, QD * 2)) rule delayed exploration behind arbitrary
        // completion counts and could end short copies before useful QD was reached.
        if (_sampleCompletions < _currentQueueDepth)
            return;
'@
if (-not $scheduler.Contains($old)) { throw 'DeviceScheduler decision interval pattern not found' }
$scheduler = $scheduler.Replace($old, $new)
Set-Content $schedulerPath $scheduler -NoNewline

$tests = Get-Content $testsPath -Raw
$anchor = @'
    [TestMethod]
    public async Task SustainedDemandCanGrowBeyondLegacyNvmeQd16()
'@
$insert = @'
    [TestMethod]
    public async Task SaturatedQdOneExploresAfterOnePhysicalQueueWindow()
    {
        using var scheduler = new DeviceScheduler("PhysicalDiskShared", 1, 64L * 1024 * 1024);
        var first = await scheduler.AcquireIoAsync(256 * 1024, CancellationToken.None);
        var waiting = scheduler.AcquireIoAsync(256 * 1024, CancellationToken.None).AsTask();

        Assert.IsFalse(waiting.IsCompleted);
        first.Dispose();

        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(2, snapshot.CurrentQueueDepth);
        Assert.AreEqual(4, snapshot.ExplorationQueueDepth);
        Assert.AreEqual(1, snapshot.QueueDepthUpshifts);
        Assert.AreEqual("increase:baseline-demand", snapshot.LastQueueDepthDecision);
    }

    [TestMethod]
    public async Task SustainedDemandCanGrowBeyondLegacyNvmeQd16()
'@
if (-not $tests.Contains($anchor)) { throw 'DeviceSchedulerTests insertion anchor not found' }
$tests = $tests.Replace($anchor, $insert)
Set-Content $testsPath $tests -NoNewline
