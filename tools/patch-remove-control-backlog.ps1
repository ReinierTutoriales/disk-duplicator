$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)

function Replace-Exact([string]$Old, [string]$New, [string]$Label) {
    if (-not $script:text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $script:text = $script:text.Replace($Old, $New)
}

Replace-Exact @'
    // This budget protects Begin/End-heavy trees. Payload data is independently
    // governed by AdaptiveByteBudget and per-device backlog/QD.
    private const int ControlBacklogCapacity = 64 * 1024;
'@ '' 'remove fixed control backlog capacity'

Replace-Exact @'
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
        var controlBudget = new GlobalControlBacklogBudget(ControlBacklogCapacity);
        using var deviceSchedulers = DeviceSchedulerMap.Create(copy.SourceDevice, copy.DestinationDevices);
'@ @'
        job.Telemetry.AttachPipelineGovernor(pipeline.Snapshot);
        using var deviceSchedulers = DeviceSchedulerMap.Create(copy.SourceDevice, copy.DestinationDevices);
'@ 'remove control budget construction'

Replace-Exact @'
                    root,
                    index,
                    progress[index],
                    controlBudget,
                    copy.DestinationDevices[index],
'@ @'
                    root,
                    index,
                    progress[index],
                    copy.DestinationDevices[index],
'@ 'remove control budget worker wiring'

$oldControl = @'
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
'@
$newControl = @'
    private static async ValueTask DeliverControlAsync(
        DestinationWorker worker,
        ControlMessage message,
        CopyJob job)
    {
        if (!worker.IsActive)
            return;

        job.Token.ThrowIfCancellationRequested();
        await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
        if (!worker.IsActive)
            return;

        worker.IncrementQueueDepth();
        if (worker.Channel.Writer.TryWrite(message))
            return;

        worker.DecrementQueueDepth();
        if (worker.IsActive)
            worker.Fail("El canal del destino se cerró antes de recibir todos los datos.");
    }
'@
Replace-Exact $oldControl $newControl 'remove control-budget admission path'

$text = $text.Replace("                if (message is ControlMessage)`r`n                    worker.ControlBudget.Release();`r`n", '')
$text = $text.Replace("            if (message is ControlMessage)`r`n                worker.ControlBudget.Release();`r`n", '')

Replace-Exact @'
            string root,
            int slot,
            DestinationProgress progress,
            GlobalControlBacklogBudget controlBudget,
            StorageDeviceInfo device,
'@ @'
            string root,
            int slot,
            DestinationProgress progress,
            StorageDeviceInfo device,
'@ 'remove control budget constructor parameter'

Replace-Exact @'
            Root = root;
            Slot = slot;
            Progress = progress;
            ControlBudget = controlBudget;
            Device = device;
'@ @'
            Root = root;
            Slot = slot;
            Progress = progress;
            Device = device;
'@ 'remove control budget assignment'

Replace-Exact @'
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public GlobalControlBacklogBudget ControlBudget { get; }
        public StorageDeviceInfo Device { get; }
'@ @'
        public int Slot { get; }
        public DestinationProgress Progress { get; }
        public StorageDeviceInfo Device { get; }
'@ 'remove control budget property'

if ($text.Contains('ControlBacklogCapacity')) { throw 'Fixed control backlog capacity remains.' }
if ($text.Contains('GlobalControlBacklogBudget')) { throw 'Control backlog budget type remains in CopyEngine.' }
if ($text.Contains('ControlBudget')) { throw 'Control budget consumer remains in CopyEngine.' }
if ($text.Contains('RecordControlBacklogWait')) { throw 'Control backlog wait telemetry call remains.' }
if ($text.Contains('ObserveControlBacklog')) { throw 'Control backlog observation remains.' }

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Control backlog removal produced no changes.' }
git commit -m 'perf(core): remove fixed control-message backlog gate'
git push origin HEAD:main
