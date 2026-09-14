$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) { throw "${Label}: expected one occurrence, found $count" }
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
        var groups = destinationArray.GroupBy(item => item.PhysicalDeviceId, StringComparer.OrdinalIgnoreCase);
        var schedulers = new Dictionary<string, DeviceScheduler>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
'@ @'
        var groups = destinationArray.GroupBy(item => item.PhysicalDeviceId, StringComparer.OrdinalIgnoreCase);
        var schedulers = new Dictionary<string, DeviceScheduler>(StringComparer.OrdinalIgnoreCase);
        DeviceScheduler? sharedSourceScheduler = null;

        foreach (var group in groups)
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
    private static async Task ProducerLoopAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        bool[][] skipMasks,
        Dictionary<string, byte[]> expectedHashes,
        CopyJob job,
        PipelineGovernor pipeline,
        AdaptiveByteBudget bufferBudget)
'@ @'
    private static async Task ProducerLoopAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        bool[][] skipMasks,
        Dictionary<string, byte[]> expectedHashes,
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
    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(
        FileEntry entry,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline)
'@ @'
    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(
        FileEntry entry,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
'@ 'sequential signature source scheduler'

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
                var readStarted = Stopwatch.GetTimestamp();
                DeviceScheduler.IoLease? sourceIo = null;
                try
                {
                    if (sharedSourceScheduler is not null)
                        sourceIo = await sharedSourceScheduler.AcquireIoAsync(readBufferSize, job.Token).ConfigureAwait(false);
                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);
                }
                finally
                {
                    sourceIo?.Dispose();
                }
                var readElapsed = Stopwatch.GetElapsedTime(readStarted);
'@ 'sequential coordinate physical source read'

Replace-Exact $engine @'
    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline)
'@ @'
    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
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
'@ 'prefetch source scheduler propagation'

Replace-Exact $engine @'
    private static async Task<SourceReadResult> PrefetchSourceAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        CancellationToken token)
'@ @'
    private static async Task<SourceReadResult> PrefetchSourceAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler,
        CancellationToken token)
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
'@ 'read-ahead source scheduler propagation'

Replace-Exact $engine @'
    private static async Task<long> ReadSourceAheadAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        CancellationToken token)
'@ @'
    private static async Task<long> ReadSourceAheadAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler,
        CancellationToken token)
'@ 'read-ahead signature source scheduler'

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
                    var readStarted = Stopwatch.GetTimestamp();
                    int read;
                    DeviceScheduler.IoLease? sourceIo = null;
                    try
                    {
                        if (sharedSourceScheduler is not null)
                            sourceIo = await sharedSourceScheduler.AcquireIoAsync(readBufferSize, token).ConfigureAwait(false);

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
                    }
                    finally
                    {
                        sourceIo?.Dispose();
                    }

                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
'@ 'read-ahead coordinate physical source read'

$text = [IO.File]::ReadAllText($engine)
if (-not $text.Contains('deviceSchedulers.SharedSourceScheduler')) { throw 'Production source scheduler wiring missing.' }
if (([regex]::Matches($text, [regex]::Escape('sharedSourceScheduler.AcquireIoAsync'))).Count -ne 2) {
    throw 'Expected exactly two physical source-read scheduler consumers.'
}

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $scheduler $engine
if (git diff --cached --quiet) { throw 'Shared physical I/O migration produced no changes.' }
git commit -m 'perf(core): coordinate shared source and destination I/O'
git push origin HEAD:main
