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

Replace-Exact $copy @'
    private static async Task DeliverAsync(
        IReadOnlyList<DestinationWorker> recipients,
        FanoutMessage message,
        bool countsData,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        if (countsData && message is DataMessage dataMessage)
'@ @'
    private static async Task DeliverAsync(
        IReadOnlyList<DestinationWorker> recipients,
        FanoutMessage message,
        CopyJob job)
    {
        if (recipients.Count == 0)
            return;

        if (message is DataMessage dataMessage)
'@ 'eliminar parámetro countsData de DeliverAsync'

Replace-Exact $copy @'
                await DeliverOneAsync(recipients[index], message, countsData, job).ConfigureAwait(false);
'@ @'
                await DeliverOneAsync(recipients[index], message, job).ConfigureAwait(false);
'@ 'migrar llamada control DeliverOne'

Replace-Exact $copy @'
                        message,
                        countsData: true,
                        job,
                        backlogReserved: true).ConfigureAwait(false);
'@ @'
                        message,
                        job,
                        backlogReserved: true).ConfigureAwait(false);
'@ 'migrar llamadas data DeliverOne'

Replace-Exact $copy @'
                    message,
                    countsData: true,
                    job,
                    backlogReserved: true).ConfigureAwait(false);
'@ @'
                    message,
                    job,
                    backlogReserved: true).ConfigureAwait(false);
'@ 'migrar llamada data diferida'

Replace-Exact $copy @'
    private static async ValueTask DeliverOneAsync(
        DestinationWorker worker,
        FanoutMessage message,
        bool countsData,
        CopyJob job,
        bool backlogReserved = false)
'@ @'
    private static async ValueTask DeliverOneAsync(
        DestinationWorker worker,
        FanoutMessage message,
        CopyJob job,
        bool backlogReserved = false)
'@ 'eliminar parámetro countsData de DeliverOne'

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
$text = $text.Replace('DeliverAsync(active, new BeginMessage(entry), countsData: false, job)', 'DeliverAsync(active, new BeginMessage(entry), job)')
$text = $text.Replace('DeliverAsync(active, new DataMessage(block), countsData: true, job)', 'DeliverAsync(active, new DataMessage(block), job)')
$text = $text.Replace('DeliverAsync(active, new DataMessage(shared), countsData: true, job)', 'DeliverAsync(active, new DataMessage(shared), job)')
$text = $text.Replace('DeliverAsync(active, new EndMessage(hash), countsData: false, job)', 'DeliverAsync(active, new EndMessage(hash), job)')
[IO.File]::WriteAllText($copy, $text, [Text.UTF8Encoding]::new($false))

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
