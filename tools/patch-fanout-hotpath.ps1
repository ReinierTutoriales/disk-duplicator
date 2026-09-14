$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$engine = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$scheduler = 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
$telemetry = 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
$tests = 'dotnet/RepartoCopier.Core.Tests/DeviceSchedulerTests.cs'
$contract = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'

Replace-Exact $engine @'
    private static async Task DeliverDataAsync(
        IReadOnlyList<DestinationWorker> recipients,
        DataMessage message,
        CopyJob job)
    {
        List<DestinationWorker> deferred = [];
        var index = 0;
        try
        {
            for (; index < recipients.Count; index++)
            {
                var worker = recipients[index];
                if (!worker.IsActive)
                {
                    message.Block.Release();
                    continue;
                }

                if (worker.DeviceScheduler.TryReserveBacklog(message.Block.Length))
                {
                    await DeliverOneAsync(
                        worker,
                        message,
                        job,
                        backlogReserved: true).ConfigureAwait(false);
                    continue;
                }

                deferred.Add(worker);
            }
        }
        catch
        {
            foreach (var _ in deferred)
                message.Block.Release();
            for (var remaining = index + 1; remaining < recipients.Count; remaining++)
                message.Block.Release();
            throw;
        }

        for (var deferredIndex = 0; deferredIndex < deferred.Count; deferredIndex++)
        {
            var worker = deferred[deferredIndex];
            var reserved = false;
            try
            {
                var waitStarted = Stopwatch.GetTimestamp();
                await worker.DeviceScheduler
                    .ReserveBacklogAsync(message.Block.Length, job.Token)
                    .ConfigureAwait(false);
                reserved = true;
                job.Telemetry.RecordQueueWait(Stopwatch.GetElapsedTime(waitStarted));

                await DeliverOneAsync(
                    worker,
                    message,
                    job,
                    backlogReserved: true).ConfigureAwait(false);
            }
            catch
            {
                if (!reserved)
                    message.Block.Release();
                for (var remaining = deferredIndex + 1; remaining < deferred.Count; remaining++)
                    message.Block.Release();
                throw;
            }
        }
    }
'@ @'
    private static async Task DeliverDataAsync(
        IReadOnlyList<DestinationWorker> recipients,
        DataMessage message,
        CopyJob job)
    {
        var index = 0;
        try
        {
            for (; index < recipients.Count; index++)
            {
                var worker = recipients[index];
                if (!worker.IsActive)
                {
                    message.Block.Release();
                    continue;
                }

                worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                await DeliverOneAsync(
                    worker,
                    message,
                    job,
                    backlogReserved: true).ConfigureAwait(false);
            }
        }
        catch
        {
            for (var remaining = index + 1; remaining < recipients.Count; remaining++)
                message.Block.Release();
            throw;
        }
    }
'@ 'single-pass data delivery'

Replace-Exact $scheduler @'
    public bool TryReserveBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            var queued = Interlocked.Read(ref _queuedBytes);
            if (queued + bytes > BacklogTargetBytes && queued != 0)
                return false;
            ReserveBacklogLocked(bytes);
            return true;
        }
    }

    public ValueTask ReserveBacklogAsync(int bytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        token.ThrowIfCancellationRequested();
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            ReserveBacklogLocked(bytes);
        }
        return ValueTask.CompletedTask;
    }
'@ @'
    public void ReserveBacklog(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_backlogGate)
        {
            ThrowIfDisposed();
            ReserveBacklogLocked(bytes);
        }
    }
'@ 'synchronous soft backlog accounting'

Replace-Exact $telemetry @'
    TimeSpan FanoutWaitTime,
    TimeSpan QueueWaitTime,
    TimeSpan ControlBacklogWaitTime,
'@ @'
    TimeSpan FanoutWaitTime,
    TimeSpan ControlBacklogWaitTime,
'@ 'remove misleading queue wait snapshot field'

Replace-Exact $telemetry @'
    private long _bufferWaitTicks, _fanoutWaitTicks, _queueWaitTicks, _controlBacklogWaitTicks;
'@ @'
    private long _bufferWaitTicks, _fanoutWaitTicks, _controlBacklogWaitTicks;
'@ 'remove queue wait accumulator'

Replace-Exact $telemetry @'
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);
    internal void RecordQueueWait(TimeSpan elapsed) => AddTicks(ref _queueWaitTicks, elapsed);
    internal void RecordControlBacklogWait(TimeSpan elapsed) => AddTicks(ref _controlBacklogWaitTicks, elapsed);
'@ @'
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);
    internal void RecordControlBacklogWait(TimeSpan elapsed) => AddTicks(ref _controlBacklogWaitTicks, elapsed);
'@ 'remove queue wait recorder'

Replace-Exact $telemetry @'
            ToTimeSpan(Interlocked.Read(ref _fanoutWaitTicks)),
            ToTimeSpan(Interlocked.Read(ref _queueWaitTicks)),
            ToTimeSpan(Interlocked.Read(ref _controlBacklogWaitTicks)),
'@ @'
            ToTimeSpan(Interlocked.Read(ref _fanoutWaitTicks)),
            ToTimeSpan(Interlocked.Read(ref _controlBacklogWaitTicks)),
'@ 'remove queue wait snapshot value'

Replace-Exact $tests @'
    [TestMethod]
    public async Task SoftBacklogTargetStillDoesNotBlockOverflowAdmission()
    {
        const int block = 8 * 1024 * 1024;
        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, block);

        Assert.IsTrue(scheduler.TryReserveBacklog(block));
        Assert.IsFalse(scheduler.TryReserveBacklog(block));
        var overflow = scheduler.ReserveBacklogAsync(block, CancellationToken.None);
        Assert.IsTrue(overflow.IsCompletedSuccessfully);
        await overflow;

        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(2L * block, snapshot.QueuedBytes);
        Assert.AreEqual(2.0, snapshot.BacklogPressure, 0.000001);
        scheduler.ReleaseBacklog(block);
        scheduler.ReleaseBacklog(block);
    }
'@ @'
    [TestMethod]
    public void SoftBacklogTargetIsAccountingOnlyAndAllowsImmediateOverflow()
    {
        const int block = 8 * 1024 * 1024;
        using var scheduler = new DeviceScheduler("PhysicalDisk3", 1, block);

        scheduler.ReserveBacklog(block);
        scheduler.ReserveBacklog(block);

        var snapshot = scheduler.Snapshot();
        Assert.AreEqual(2L * block, snapshot.QueuedBytes);
        Assert.AreEqual(2.0, snapshot.BacklogPressure, 0.000001);
        scheduler.ReleaseBacklog(block);
        scheduler.ReleaseBacklog(block);
    }
'@ 'migrate soft backlog test'

Replace-Exact $contract @'
    [TestMethod]
    public void PayloadMessagesStayOutsideControlBacklogBudgetArchitecture()
'@ @'
    [TestMethod]
    public void ObsoleteBacklogAdmissionAndQueueWaitTelemetryStayRemoved()
    {
        var schedulerMethods = typeof(DeviceScheduler)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(schedulerMethods, "TryReserveBacklog");
        CollectionAssert.DoesNotContain(schedulerMethods, "ReserveBacklogAsync");
        CollectionAssert.Contains(schedulerMethods, "ReserveBacklog");

        var diagnosticsProperties = typeof(CopyDiagnosticsSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(diagnosticsProperties, "QueueWaitTime");
    }

    [TestMethod]
    public void PayloadMessagesStayOutsideControlBacklogBudgetArchitecture()
'@ 'architecture contract for hot-path cleanup'

$negativeContract = [IO.Path]::GetFullPath($contract)
$legacy = @()
Get-ChildItem 'dotnet' -Recurse -Filter '*.cs' | Where-Object { $_.FullName -ne $negativeContract } | ForEach-Object {
    $matches = Select-String -Path $_.FullName -Pattern 'TryReserveBacklog|ReserveBacklogAsync|RecordQueueWait|QueueWaitTime|_queueWaitTicks|List<DestinationWorker> deferred'
    foreach ($match in $matches) {
        $relative = [IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName)
        $legacy += "${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}
if ($legacy.Count -gt 0) {
    $legacy | ForEach-Object { Write-Host $_ }
    throw "Obsolete FAN-OUT hot-path symbols remain: $($legacy.Count) occurrence(s)."
}

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $engine $scheduler $telemetry $tests $contract
if (git diff --cached --quiet) { throw 'FAN-OUT hot-path migration produced no changes.' }
git commit -m 'perf(core): simplify FAN-OUT backlog admission hot path'
git push origin HEAD:main
