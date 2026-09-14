$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$scheduler = 'dotnet/RepartoCopier.Core/DeviceScheduler.cs'
$engine = 'dotnet/RepartoCopier.Core/CopyEngine.cs'

Replace-Exact $scheduler @'
internal sealed class DeviceSchedulerMap : IDisposable
{
    private readonly Dictionary<string, DeviceScheduler> _schedulers;

    private DeviceSchedulerMap(Dictionary<string, DeviceScheduler> schedulers) =>
        _schedulers = schedulers;

    public IReadOnlyCollection<DeviceScheduler> Schedulers => _schedulers.Values;
'@ @'
internal sealed class DeviceSchedulerMap : IDisposable
{
    private readonly Dictionary<string, DeviceScheduler> _schedulers;

    private DeviceSchedulerMap(
        Dictionary<string, DeviceScheduler> schedulers,
        DeviceScheduler? sharedSourceScheduler)
    {
        _schedulers = schedulers;
        SharedSourceScheduler = sharedSourceScheduler;
    }

    public IReadOnlyCollection<DeviceScheduler> Schedulers => _schedulers.Values;
    public DeviceScheduler? SharedSourceScheduler { get; }
'@ 'map shared-source property'

Replace-Exact $scheduler @'
        var schedulers = new Dictionary<string, DeviceScheduler>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
'@ @'
        var schedulers = new Dictionary<string, DeviceScheduler>(StringComparer.OrdinalIgnoreCase);
        DeviceScheduler? sharedSourceScheduler = null;

        foreach (var group in groups)
        {
'@ 'map shared-source local'

Replace-Exact $scheduler @'
            // Sharing source/destination on one physical disk starts cautiously
            // to avoid immediate seek thrash, but it is not a permanent QD1 cap.
            if (source is not null && SharesPhysicalDevice(source, materializedGroup[0]))
                initialDepth = 1;

            schedulers.Add(
                group.Key,
                new DeviceScheduler(group.Key, initialDepth, backlogTarget, confidence));
        }

        return new DeviceSchedulerMap(schedulers);
'@ @'
            // Sharing source/destination on one physical disk starts cautiously
            // to avoid immediate seek thrash, but it is not a permanent QD1 cap.
            var sharesSource = source is not null && SharesPhysicalDevice(source, materializedGroup[0]);
            if (sharesSource)
                initialDepth = 1;

            var scheduler = new DeviceScheduler(group.Key, initialDepth, backlogTarget, confidence);
            schedulers.Add(group.Key, scheduler);
            if (sharesSource)
                sharedSourceScheduler = scheduler;
        }

        return new DeviceSchedulerMap(schedulers, sharedSourceScheduler);
'@ 'map shared scheduler creation'

Replace-Exact $engine @'
                await ProducerLoopAsync(copy, workers, progress, skipMasks, expectedHashes, job, pipeline, bufferBudget).ConfigureAwait(false);
'@ @'
                await ProducerLoopAsync(
                    copy,
                    workers,
                    progress,
                    skipMasks,
                    expectedHashes,
                    job,
                    pipeline,
                    bufferBudget,
                    deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);
'@ 'production producer source scheduler wiring'

Replace-Exact $engine @'
        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget)
'@ @'
        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget,
        DeviceScheduler? sharedSourceScheduler)
'@ 'producer signature source scheduler'

Replace-Exact $engine @'
                var sourceResult = entry.Size >= SourcePrefetchThreshold
                    ? await ReadAndFanOutPrefetchedAsync(entry, copy.SourceDevice, active, bufferBudget, job, pipeline).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(entry, active, bufferBudget, job, pipeline).ConfigureAwait(false);
'@ @'
                var sourceResult = entry.Size >= SourcePrefetchThreshold
                    ? await ReadAndFanOutPrefetchedAsync(
                        entry,
                        copy.SourceDevice,
                        active,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false)
                    : await ReadAndFanOutSequentialAsync(
                        entry,
                        active,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false);
'@ 'fanout source scheduler propagation'

Replace-Exact $engine @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline)
    {
'@ @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
    {
'@ 'sequential signature source scheduler'

Replace-Exact $engine @'
        while (true)
        {
            job.Token.ThrowIfCancellationRequested();
'@ @'
        while (totalRead < entry.Size)
        {
            job.Token.ThrowIfCancellationRequested();
'@ 'sequential avoid redundant EOF read'

Replace-Exact $engine @'
            int read;
            try
            {
                var readStarted = Stopwatch.GetTimestamp();
                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);
                var readElapsed = Stopwatch.GetElapsedTime(readStarted);
'@ @'
            int read;
            try
            {
                var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));
                var readStarted = Stopwatch.GetTimestamp();
                DeviceScheduler.IoLease? sourceIo = null;
                try
                {
                    if (sharedSourceScheduler is not null)
                        sourceIo = await sharedSourceScheduler.AcquireIoAsync(remaining, job.Token).ConfigureAwait(false);
                    read = await source.ReadAsync(rented.AsMemory(0, remaining), job.Token).ConfigureAwait(false);
                }
                finally
                {
                    sourceIo?.Dispose();
                }
                var readElapsed = Stopwatch.GetElapsedTime(readStarted);
'@ 'sequential coordinate physical source read'

Replace-Exact $engine @'
            if (read == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                break;
            }
'@ @'
            if (read == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                throw new IOException($"Lectura incompleta del origen: {entry.RelativePath}");
            }
'@ 'sequential early EOF fail'

Replace-Exact $engine @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline)
    {
        using var prefetchCancel = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
'@ @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
    {
        using var prefetchCancel = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
'@ 'prefetched signature source scheduler'

Replace-Exact $engine @'
            bufferBudget,
            job,
            pipeline,
            prefetchCancel.Token);
'@ @'
            bufferBudget,
            job,
            pipeline,
            sharedSourceScheduler,
            prefetchCancel.Token);
'@ 'prefetch propagation source scheduler'

Replace-Exact $engine @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        CancellationToken token)
    {
'@ @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler,
        CancellationToken token)
    {
'@ 'prefetch stage signature source scheduler'

Replace-Exact $engine @'
            bufferBudget,
            job,
            pipeline,
            stageCancel.Token);
'@ @'
            bufferBudget,
            job,
            pipeline,
            sharedSourceScheduler,
            stageCancel.Token);
'@ 'read-ahead propagation source scheduler'

# Replace only the ReadSourceAheadAsync signature occurrence that remains.
$text = [IO.File]::ReadAllText($engine)
$oldSig = @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        CancellationToken token)
    {
        Exception? completionError = null;
        DirectIoSourceReader.OverlappedSession? direct = null;
'@
$newSig = @'
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler,
        CancellationToken token)
    {
        Exception? completionError = null;
        DirectIoSourceReader.OverlappedSession? direct = null;
'@
if (-not $text.Contains($oldSig)) { throw 'read-ahead signature source scheduler: exact pattern not found' }
$text = $text.Replace($oldSig, $newSig)
[IO.File]::WriteAllText($engine, $text, [Text.UTF8Encoding]::new($false))

Replace-Exact $engine @'
            long totalRead = 0;
            while (true)
            {
'@ @'
            long totalRead = 0;
            while (totalRead < entry.Size)
            {
'@ 'read-ahead avoid redundant EOF read'

Replace-Exact $engine @'
                    var readStarted = Stopwatch.GetTimestamp();
                    int read;
                    if (direct is not null)
                    {
                        try
                        {
                            read = await direct.ReadAsync(lease, readBufferSize, totalRead, token).ConfigureAwait(false);
                            job.Telemetry.RecordDirectSourceRead(read);
                        }
                        catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                        {
                            lease.Dispose();
                            lease = null;
                            direct.Dispose();
                            direct = null;
                            job.Telemetry.RecordDirectSourceFallback();
                            buffered = OpenSourceStream(entry.SourcePath);
                            buffered.Position = totalRead;
                            lease = SourceBufferLease.RentBuffered(readBufferSize);
                            read = await buffered.ReadAsync(lease.Memory, token).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        read = await buffered!.ReadAsync(lease.Memory, token).ConfigureAwait(false);
                    }

                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
'@ @'
                    var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));
                    var readStarted = Stopwatch.GetTimestamp();
                    int read;
                    DeviceScheduler.IoLease? sourceIo = null;
                    try
                    {
                        if (sharedSourceScheduler is not null)
                            sourceIo = await sharedSourceScheduler.AcquireIoAsync(remaining, token).ConfigureAwait(false);

                        if (direct is not null)
                        {
                            try
                            {
                                read = await direct.ReadAsync(lease, readBufferSize, totalRead, token).ConfigureAwait(false);
                                job.Telemetry.RecordDirectSourceRead(read);
                            }
                            catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                            {
                                lease.Dispose();
                                lease = null;
                                direct.Dispose();
                                direct = null;
                                job.Telemetry.RecordDirectSourceFallback();
                                buffered = OpenSourceStream(entry.SourcePath);
                                buffered.Position = totalRead;
                                lease = SourceBufferLease.RentBuffered(readBufferSize);
                                read = await buffered.ReadAsync(lease.Memory[..remaining], token).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            read = await buffered!.ReadAsync(lease.Memory[..remaining], token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        sourceIo?.Dispose();
                    }

                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
'@ 'read-ahead coordinate physical source read'

Replace-Exact $engine @'
                    if (read == 0)
                    {
                        lease.Dispose();
                        lease = null;
                        bufferBudget.Release(readBufferSize);
                        budgetOwned = false;
                        pipeline.ReleasePrefetchSlot();
                        slotOwned = false;
                        break;
                    }
'@ @'
                    if (read == 0)
                    {
                        lease.Dispose();
                        lease = null;
                        bufferBudget.Release(readBufferSize);
                        budgetOwned = false;
                        pipeline.ReleasePrefetchSlot();
                        slotOwned = false;
                        throw new IOException($"Lectura incompleta del origen: {entry.RelativePath}");
                    }
'@ 'read-ahead early EOF fail'

# Permanent sanity audit for the migrated production surface.
$text = [IO.File]::ReadAllText($engine)
if (-not $text.Contains('deviceSchedulers.SharedSourceScheduler')) { throw 'Production source scheduler wiring missing.' }
if (-not $text.Contains('sharedSourceScheduler.AcquireIoAsync')) { throw 'Source reads do not consume the shared physical scheduler.' }

[IO.File]::WriteAllText($engine, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $scheduler $engine
if (git diff --cached --quiet) { throw 'Shared physical I/O migration produced no changes.' }
git commit -m 'perf(core): coordinate shared source and destination I/O'
git push origin HEAD:main
