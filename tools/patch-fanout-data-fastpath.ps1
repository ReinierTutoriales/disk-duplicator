$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$engine = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$contract = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'

Replace-Exact $engine @'
    private static async Task DeliverAsync(
        IReadOnlyList<DestinationWorker> recipients,
        FanoutMessage message,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        if (message is DataMessage dataMessage)
        {
            await DeliverDataAsync(recipients, dataMessage, job).ConfigureAwait(false);
            return;
        }

        var index = 0;
        try
        {
            for (; index < recipients.Count; index++)
                await DeliverOneAsync(recipients[index], message, job).ConfigureAwait(false);
        }
        catch
        {
            if (message is DataMessage data)
            {
                for (var remaining = index + 1; remaining < recipients.Count; remaining++)
                    data.Block.Release();
            }
            throw;
        }
    }

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

    private static async ValueTask DeliverOneAsync(
        DestinationWorker worker,
        FanoutMessage message,
        CopyJob job,
        bool backlogReserved = false)
    {
        if (!worker.IsActive)
        {
            if (backlogReserved && message is DataMessage inactiveData)
                worker.DeviceScheduler.ReleaseBacklog(inactiveData.Block.Length);
            ReleaseIfData(message);
            return;
        }

        var controlOwned = false;
        var queueOwned = false;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            if (message is ControlMessage)
            {
                var controlWaitStarted = Stopwatch.GetTimestamp();
                await worker.ControlBudget.AcquireAsync(job.Token).ConfigureAwait(false);
                var controlWait = Stopwatch.GetElapsedTime(controlWaitStarted);
                job.Telemetry.RecordControlBacklogWait(controlWait);
                job.Telemetry.ObserveControlBacklog(worker.ControlBudget.Used);
                controlOwned = true;
            }

            if (!worker.IsActive)
            {
                if (controlOwned)
                {
                    worker.ControlBudget.Release();
                    controlOwned = false;
                }
                if (backlogReserved && message is DataMessage inactiveData)
                    worker.DeviceScheduler.ReleaseBacklog(inactiveData.Block.Length);
                ReleaseIfData(message);
                return;
            }

            worker.IncrementQueueDepth();
            queueOwned = true;

            if (worker.Channel.Writer.TryWrite(message))
            {
                controlOwned = false;
                queueOwned = false;
                return;
            }

            worker.DecrementQueueDepth();
            queueOwned = false;
            if (controlOwned)
            {
                worker.ControlBudget.Release();
                controlOwned = false;
            }
            if (backlogReserved && message is DataMessage rejectedData)
                worker.DeviceScheduler.ReleaseBacklog(rejectedData.Block.Length);
            ReleaseIfData(message);
            if (worker.IsActive)
                worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        catch (OperationCanceledException)
        {
            if (queueOwned) worker.DecrementQueueDepth();
            if (controlOwned) worker.ControlBudget.Release();
            if (backlogReserved && message is DataMessage cancelledData)
                worker.DeviceScheduler.ReleaseBacklog(cancelledData.Block.Length);
            ReleaseIfData(message);
            throw;
        }
        catch
        {
            if (queueOwned) worker.DecrementQueueDepth();
            if (controlOwned) worker.ControlBudget.Release();
            if (backlogReserved && message is DataMessage failedData)
                worker.DeviceScheduler.ReleaseBacklog(failedData.Block.Length);
            ReleaseIfData(message);
            throw;
        }
    }
'@ @'
    private static async Task DeliverAsync(
        IReadOnlyList<DestinationWorker> recipients,
        FanoutMessage message,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        if (message is DataMessage dataMessage)
        {
            await DeliverDataAsync(recipients, dataMessage, job).ConfigureAwait(false);
            return;
        }

        var control = (ControlMessage)message;
        for (var index = 0; index < recipients.Count; index++)
            await DeliverControlAsync(recipients[index], control, job).ConfigureAwait(false);
    }

    private static async Task DeliverDataAsync(
        IReadOnlyList<DestinationWorker> recipients,
        DataMessage message,
        CopyJob job)
    {
        var index = -1;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            for (index = 0; index < recipients.Count; index++)
            {
                var worker = recipients[index];
                if (!worker.IsActive)
                {
                    message.Block.Release();
                    continue;
                }

                var backlogOwned = false;
                var queueOwned = false;
                var blockOwned = true;
                try
                {
                    worker.DeviceScheduler.ReserveBacklog(message.Block.Length);
                    backlogOwned = true;

                    if (!worker.IsActive)
                    {
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                        backlogOwned = false;
                        message.Block.Release();
                        blockOwned = false;
                        continue;
                    }

                    worker.IncrementQueueDepth();
                    queueOwned = true;
                    if (worker.Channel.Writer.TryWrite(message))
                    {
                        queueOwned = false;
                        backlogOwned = false;
                        blockOwned = false;
                        continue;
                    }

                    worker.DecrementQueueDepth();
                    queueOwned = false;
                    worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    backlogOwned = false;
                    message.Block.Release();
                    blockOwned = false;
                    if (worker.IsActive)
                        worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
                }
                catch
                {
                    if (queueOwned)
                        worker.DecrementQueueDepth();
                    if (backlogOwned)
                        worker.DeviceScheduler.ReleaseBacklog(message.Block.Length);
                    if (blockOwned)
                        message.Block.Release();
                    throw;
                }
            }
        }
        catch
        {
            for (var remaining = index + 1; remaining < recipients.Count; remaining++)
                message.Block.Release();
            throw;
        }
    }

    private static async ValueTask DeliverControlAsync(
        DestinationWorker worker,
        ControlMessage message,
        CopyJob job)
    {
        if (!worker.IsActive)
            return;

        var controlOwned = false;
        var queueOwned = false;
        try
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            var controlWaitStarted = Stopwatch.GetTimestamp();
            await worker.ControlBudget.AcquireAsync(job.Token).ConfigureAwait(false);
            var controlWait = Stopwatch.GetElapsedTime(controlWaitStarted);
            job.Telemetry.RecordControlBacklogWait(controlWait);
            job.Telemetry.ObserveControlBacklog(worker.ControlBudget.Used);
            controlOwned = true;

            if (!worker.IsActive)
            {
                worker.ControlBudget.Release();
                return;
            }

            worker.IncrementQueueDepth();
            queueOwned = true;
            if (worker.Channel.Writer.TryWrite(message))
            {
                controlOwned = false;
                queueOwned = false;
                return;
            }

            worker.DecrementQueueDepth();
            queueOwned = false;
            worker.ControlBudget.Release();
            controlOwned = false;
            if (worker.IsActive)
                worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
        }
        catch
        {
            if (queueOwned)
                worker.DecrementQueueDepth();
            if (controlOwned)
                worker.ControlBudget.Release();
            throw;
        }
    }
'@ 'dedicated data fast path and control route'

Replace-Exact $contract @'
        var deliveryMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name is "DeliverAsync" or "DeliverOneAsync")
            .ToArray();
        Assert.IsGreaterThan(0, deliveryMethods.Length);
        Assert.IsFalse(deliveryMethods
            .SelectMany(method => method.GetParameters())
            .Any(parameter => string.Equals(parameter.Name, "countsData", StringComparison.Ordinal)));
'@ @'
        var deliveryMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name is "DeliverAsync" or "DeliverDataAsync" or "DeliverControlAsync")
            .ToArray();
        Assert.AreEqual(3, deliveryMethods.Length);
        Assert.IsFalse(deliveryMethods
            .SelectMany(method => method.GetParameters())
            .Any(parameter => string.Equals(parameter.Name, "countsData", StringComparison.Ordinal)));

        var deliverControl = deliveryMethods.Single(method => method.Name == "DeliverControlAsync");
        Assert.AreEqual("ControlMessage", deliverControl.GetParameters()[1].ParameterType.Name);
        var engineMethods = typeof(CopyEngine)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.DoesNotContain(engineMethods, "DeliverOneAsync");
'@ 'lock dedicated payload/control delivery routes'

$legacy = @()
Get-ChildItem 'dotnet' -Recurse -Filter '*.cs' | ForEach-Object {
    $matches = Select-String -Path $_.FullName -Pattern 'backlogReserved|DeliverOneAsync'
    foreach ($match in $matches) {
        $relative = [IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName)
        $legacy += "${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}
if ($legacy.Count -gt 0) {
    $legacy | ForEach-Object { Write-Host $_ }
    throw "Legacy per-destination data delivery route remains: $($legacy.Count) occurrence(s)."
}

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $engine $contract
if (git diff --cached --quiet) { throw 'FAN-OUT data fast-path migration produced no changes.' }
git commit -m 'perf(core): specialize FAN-OUT data delivery fast path'
git push origin HEAD:main
