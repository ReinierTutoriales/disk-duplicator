$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: patrón exacto no encontrado" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$copy = 'dotnet/RepartoCopier.Core/CopyEngine.cs'

Replace-Exact $copy @'
    private abstract record FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : FanoutMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : FanoutMessage;
'@ @'
    private abstract record FanoutMessage;
    private abstract record ControlMessage : FanoutMessage;
    private sealed record BeginMessage(FileEntry Entry) : ControlMessage;
    private sealed record DataMessage(SharedBlock Block) : FanoutMessage;
    private sealed record EndMessage(byte[] Hash) : ControlMessage;
'@ 'clasificar mensajes de control'

Replace-Exact $copy '        bool countsData,`n        CopyJob job)' '        CopyJob job)' 'eliminar countsData de DeliverAsync'
Replace-Exact $copy '        if (countsData && message is DataMessage dataMessage)' '        if (message is DataMessage dataMessage)' 'tipar data en DeliverAsync'
Replace-Exact $copy '                await DeliverOneAsync(recipients[index], message, countsData, job).ConfigureAwait(false);' '                await DeliverOneAsync(recipients[index], message, job).ConfigureAwait(false);' 'migrar control DeliverOne'

$text = [IO.File]::ReadAllText($copy)
$text = $text.Replace("                        countsData: true,`n                        job,", "                        job,")
$text = $text.Replace("                    countsData: true,`n                    job,", "                    job,")
$text = $text.Replace("        bool countsData,`n        CopyJob job,`n        bool backlogReserved = false)", "        CopyJob job,`n        bool backlogReserved = false)")
$text = $text.Replace('DeliverAsync(active, new BeginMessage(entry), countsData: false, job)', 'DeliverAsync(active, new BeginMessage(entry), job)')
$text = $text.Replace('DeliverAsync(active, new DataMessage(block), countsData: true, job)', 'DeliverAsync(active, new DataMessage(block), job)')
$text = $text.Replace('DeliverAsync(active, new DataMessage(shared), countsData: true, job)', 'DeliverAsync(active, new DataMessage(shared), job)')
$text = $text.Replace('DeliverAsync(active, new EndMessage(hash), countsData: false, job)', 'DeliverAsync(active, new EndMessage(hash), job)')
[IO.File]::WriteAllText($copy, $text, [Text.UTF8Encoding]::new($false))

Replace-Exact $copy @'
            var controlWaitStarted = Stopwatch.GetTimestamp();
            await worker.ControlBudget.AcquireAsync(job.Token).ConfigureAwait(false);
            var controlWait = Stopwatch.GetElapsedTime(controlWaitStarted);
            job.Telemetry.RecordControlBacklogWait(controlWait);
            job.Telemetry.ObserveControlBacklog(worker.ControlBudget.Used);
            controlOwned = true;
'@ @'
            if (message is ControlMessage)
            {
                var controlWaitStarted = Stopwatch.GetTimestamp();
                await worker.ControlBudget.AcquireAsync(job.Token).ConfigureAwait(false);
                var controlWait = Stopwatch.GetElapsedTime(controlWaitStarted);
                job.Telemetry.RecordControlBacklogWait(controlWait);
                job.Telemetry.ObserveControlBacklog(worker.ControlBudget.Used);
                controlOwned = true;
            }
'@ 'data no consume control budget'

Replace-Exact $copy @'
            if (!worker.IsActive)
            {
                worker.ControlBudget.Release();
                controlOwned = false;
'@ @'
            if (!worker.IsActive)
            {
                if (controlOwned)
                {
                    worker.ControlBudget.Release();
                    controlOwned = false;
                }
'@ 'liberar control solo si adquirido al quedar inactivo'

Replace-Exact $copy @'
            worker.DecrementQueueDepth();
            queueOwned = false;
            worker.ControlBudget.Release();
            controlOwned = false;
'@ @'
            worker.DecrementQueueDepth();
            queueOwned = false;
            if (controlOwned)
            {
                worker.ControlBudget.Release();
                controlOwned = false;
            }
'@ 'liberar control solo si adquirido al rechazar canal'

Replace-Exact $copy @'
                worker.DecrementQueueDepth();
                worker.ControlBudget.Release();
                var data = message as DataMessage;
'@ @'
                worker.DecrementQueueDepth();
                if (message is ControlMessage)
                    worker.ControlBudget.Release();
                var data = message as DataMessage;
'@ 'writer libera solo mensajes de control'

Replace-Exact $copy @'
            worker.DecrementQueueDepth();
            worker.ControlBudget.Release();
            if (message is DataMessage data)
'@ @'
            worker.DecrementQueueDepth();
            if (message is ControlMessage)
                worker.ControlBudget.Release();
            if (message is DataMessage data)
'@ 'drain libera solo mensajes de control'

$text = [IO.File]::ReadAllText($copy)
if ($text.Contains('countsData')) { throw 'countsData sigue presente' }
if (-not $text.Contains('private abstract record ControlMessage : FanoutMessage;')) { throw 'ControlMessage no quedó integrado' }
if (-not $text.Contains('if (message is ControlMessage)')) { throw 'ControlBudget no está tipado por mensaje' }

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $copy
if (git diff --cached --quiet) { throw 'La separación control/data no produjo cambios.' }
git commit -m 'perf(core): decouple payload data from control backlog budget'
git push origin HEAD:main
