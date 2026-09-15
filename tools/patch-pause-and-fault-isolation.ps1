$ErrorActionPreference = 'Stop'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText((Resolve-Path $Path)) }
function Write-Text([string]$Path, [string]$Content) { [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, [System.Text.UTF8Encoding]::new($false)) }
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if (-not $text.Contains($Old)) { throw "Expected anchor missing in $Path`n$Old" }
    Write-Text $Path ($text.Replace($Old, $New))
}

$ui = 'dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs'
Replace-Exact $ui @'
        if (paused)
        {
            OperationTitleText.Text = "Pausado";
            StatusText.Text = "Pausado";
            return;
        }
'@ @'
        if (paused)
        {
            OperationTitleText.Text = "Pausado";
            StatusText.Text = "Pausado";
            RefreshProgress();
            return;
        }
'@

Replace-Exact $ui @'
        var snapshots = _job.Snapshot();
        while (_progressRows.Count < snapshots.Count)
            _progressRows.Add(new ProgressRow(snapshots[_progressRows.Count]));
        for (var index = 0; index < snapshots.Count; index++)
            _progressRows[index].Update(snapshots[index]);

        var verifying = snapshots.Any(item => item.Phase == DestinationPhase.Verifying);
'@ @'
        var snapshots = _job.Snapshot();
        var paused = _job.IsPaused;
        while (_progressRows.Count < snapshots.Count)
            _progressRows.Add(new ProgressRow(snapshots[_progressRows.Count], paused));
        for (var index = 0; index < snapshots.Count; index++)
            _progressRows[index].Update(snapshots[index], paused);

        var verifying = snapshots.Any(item => item.Phase == DestinationPhase.Verifying);
'@

Replace-Exact $ui @'
            SpeedMetricText.Text = "—";
            RemainingMetricText.Text = "--:--:--";
'@ @'
            SpeedMetricText.Text = paused ? "0.0 B/s" : "—";
            RemainingMetricText.Text = "--:--:--";
'@

Replace-Exact $ui @'
            var speed = snapshots.Aggregate<DestinationSnapshot, double>(0d, (sum, item) => sum + item.RecentBytesPerSecond);
            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);
            OverallDetailText.Text = $"{FormatBytes(written)} de {FormatBytes(total)}";
            SpeedMetricText.Text = Throughput.Format(speed);
            var remaining = total > written ? total - written : 0;
            RemainingMetricText.Text = speed > 1
                ? FormatDuration(TimeSpan.FromSeconds(remaining / speed))
                : "--:--:--";
'@ @'
            var speed = paused
                ? 0d
                : snapshots.Aggregate<DestinationSnapshot, double>(0d, (sum, item) => sum + item.RecentBytesPerSecond);
            percent = total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 100);
            OverallDetailText.Text = $"{FormatBytes(written)} de {FormatBytes(total)}";
            SpeedMetricText.Text = paused ? "0.0 B/s" : Throughput.Format(speed);
            var remaining = total > written ? total - written : 0;
            RemainingMetricText.Text = !paused && speed > 1
                ? FormatDuration(TimeSpan.FromSeconds(remaining / speed))
                : "--:--:--";
'@

Replace-Exact $ui @'
        public ProgressRow(DestinationSnapshot snapshot) => Update(snapshot);
'@ @'
        public ProgressRow(DestinationSnapshot snapshot, bool paused = false) => Update(snapshot, paused);
'@

Replace-Exact $ui @'
        public void Update(DestinationSnapshot snapshot)
'@ @'
        public void Update(DestinationSnapshot snapshot, bool paused = false)
'@

Replace-Exact $ui @'
            Speed = snapshot.Phase == DestinationPhase.Verifying
                ? "—"
                : Throughput.Format(snapshot.RecentBytesPerSecond);
'@ @'
            Speed = paused
                ? "0.0 B/s"
                : snapshot.Phase == DestinationPhase.Verifying
                    ? "—"
                    : Throughput.Format(snapshot.RecentBytesPerSecond);
'@

$tests = 'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs'
Replace-Exact $tests @'
    [TestMethod]
    public async Task FanOutStressWithRapidPauseResumePreservesEveryDestination()
'@ @'
    [TestMethod]
    public async Task CancellationWhilePausedUnblocksGateAndCancelsWaiter()
    {
        await using var job = new CopyJob(Array.Empty<DestinationProgress>());
        job.SetPaused(true);
        var blocked = job.WaitIfPausedAsync(job.Token).AsTask();
        Assert.IsFalse(blocked.IsCompleted);

        job.RequestCancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await blocked);
        Assert.IsFalse(job.IsPaused);
    }

    [TestMethod]
    public async Task LockedDestinationCommitFailsOnlyThatBranchAndHealthyBranchCompletes()
    {
        using var temp = new TempDirectory("branch-fault-isolation");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[4 * 1024 * 1024 + 257];
        new Random(424242).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);

        var blockedBase = Directory.CreateDirectory(Path.Combine(temp.Path, "blocked-dest")).FullName;
        var healthyBase = Directory.CreateDirectory(Path.Combine(temp.Path, "healthy-dest")).FullName;
        var blockedRoot = Directory.CreateDirectory(Path.Combine(blockedBase, "Origen")).FullName;
        var blockedFile = Path.Combine(blockedRoot, "payload.bin");
        await File.WriteAllTextAsync(blockedFile, "old-version");

        using (var held = new FileStream(blockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var plan = CopyPlan.Create(source, [blockedBase, healthyBase], skipSame: false, keepGoing: false);
            await using var job = CopyEngine.Start(plan);
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));

            var snapshots = job.Snapshot();
            Assert.AreEqual(1, snapshots.Count(item => item.Phase == DestinationPhase.Done));
            Assert.AreEqual(1, snapshots.Count(item => item.Phase == DestinationPhase.Failed));
            CollectionAssert.AreEqual(
                payload,
                await File.ReadAllBytesAsync(Path.Combine(healthyBase, "Origen", "payload.bin")));
            Assert.AreEqual("old-version", await File.ReadAllTextAsync(blockedFile));
        }

        var retryPlan = CopyPlan.Create(source, [blockedBase], skipSame: false, keepGoing: false);
        await using var retryJob = CopyEngine.Start(retryPlan);
        await retryJob.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        AssertHealthy(retryJob);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(blockedFile));
    }

    [TestMethod]
    public async Task FanOutStressWithRapidPauseResumePreservesEveryDestination()
'@

Write-Host 'Pause UI and fault-isolation tests patched.'
