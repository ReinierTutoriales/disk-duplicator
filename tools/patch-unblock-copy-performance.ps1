$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)

function Replace-Exact([string]$Old, [string]$New, [string]$Label) {
    if (-not $script:text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $script:text = $script:text.Replace($Old, $New)
}

Replace-Exact @'
    private const int SourcePrefetchThreshold = 16 * 1024 * 1024;
'@ '' 'remove fixed source prefetch threshold'

Replace-Exact @'
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
'@ @'
                var readBufferSize = ReadBufferSizeFor(entry.Size);
                var sourceResult = entry.Size > readBufferSize
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
                        copy.SourceDevice,
                        active,
                        bufferBudget,
                        job,
                        pipeline,
                        sharedSourceScheduler).ConfigureAwait(false);
'@ 'replace fixed prefetch threshold with physical multi-block decision'

$oldSequential = @'
    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(
        FileEntry entry,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
    {
        using var hasher = Hasher.New();
        await using var source = OpenSourceStream(entry.SourcePath);
        var readBufferSize = ReadBufferSizeFor(entry.Size);
        long totalRead = 0;

        while (true)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
            var budgetStarted = Stopwatch.GetTimestamp();
            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);
            var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
            pipeline.RecordBudgetWait(budgetElapsed);
            job.Telemetry.RecordBufferWait(budgetElapsed);
            job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);
            var rented = ArrayPool<byte>.Shared.Rent(readBufferSize);
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
                pipeline.RecordSourceRead(readElapsed);
                job.Telemetry.RecordSourceRead(read, readElapsed);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                throw;
            }

            if (read == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                break;
            }

            totalRead += read;
            var hashStarted = Stopwatch.GetTimestamp();
            hasher.UpdateWithJoin(rented.AsSpan(0, read));
            job.Telemetry.RecordSourceHash(read, Stopwatch.GetElapsedTime(hashStarted));
            active.RemoveAll(worker => !worker.IsActive);
            if (active.Count == 0)
            {
                ArrayPool<byte>.Shared.Return(rented);
                bufferBudget.Release(readBufferSize);
                return null;
            }

            var block = new SharedBlock(rented, read, readBufferSize, active.Count, bufferBudget);
            var deliveryStarted = Stopwatch.GetTimestamp();
            await DeliverAsync(active, new DataMessage(block), job).ConfigureAwait(false);
            var deliveryElapsed = Stopwatch.GetElapsedTime(deliveryStarted);
            pipeline.RecordDeliveryWait(deliveryElapsed);
            job.Telemetry.RecordFanoutWait(deliveryElapsed);
            active.RemoveAll(worker => !worker.IsActive);
            if (active.Count == 0)
                return null;
        }

        ValidateCompletedSourceRead(entry, totalRead);
        return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
    }
'@
$newSequential = @'
    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(
        FileEntry entry,
        StorageDeviceInfo sourceDevice,
        List<DestinationWorker> active,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        DeviceScheduler? sharedSourceScheduler)
    {
        using var hasher = Hasher.New();
        var readBufferSize = ReadBufferSizeFor(entry.Size);
        DirectIoSourceReader.OverlappedSession? direct = null;
        FileStream? buffered = null;
        try
        {
            if (!DirectIoSourceReader.TryOpenOverlapped(entry.SourcePath, sourceDevice, readBufferSize, out direct))
                buffered = OpenSourceStream(entry.SourcePath);

            long totalRead = 0;
            while (totalRead < entry.Size)
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                var budgetStarted = Stopwatch.GetTimestamp();
                await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);
                var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
                pipeline.RecordBudgetWait(budgetElapsed);
                job.Telemetry.RecordBufferWait(budgetElapsed);
                job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);

                SourceBufferLease? lease = SourceBufferLease.RentAligned(
                    readBufferSize,
                    DirectIoSourceReader.MaximumSupportedAlignment);
                var budgetOwned = true;
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

                        if (direct is not null)
                        {
                            try
                            {
                                read = await direct.ReadAsync(lease, readBufferSize, totalRead, job.Token).ConfigureAwait(false);
                                job.Telemetry.RecordDirectSourceRead(read);
                            }
                            catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                            {
                                direct.Dispose();
                                direct = null;
                                job.Telemetry.RecordDirectSourceFallback();
                                buffered = OpenSourceStream(entry.SourcePath);
                                buffered.Position = totalRead;
                                read = await buffered.ReadAsync(lease.Memory[..remaining], job.Token).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            read = await buffered!.ReadAsync(lease.Memory[..remaining], job.Token).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        sourceIo?.Dispose();
                    }
                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                    pipeline.RecordSourceRead(readElapsed);
                    job.Telemetry.RecordSourceRead(read, readElapsed);

                    if (read == 0)
                        throw new IOException($"Lectura incompleta del origen: {entry.RelativePath}");

                    totalRead += read;
                    var hashStarted = Stopwatch.GetTimestamp();
                    hasher.UpdateWithJoin(lease.Memory.Span[..read]);
                    job.Telemetry.RecordSourceHash(read, Stopwatch.GetElapsedTime(hashStarted));
                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0)
                        return null;

                    var block = new SharedBlock(lease, read, readBufferSize, active.Count, bufferBudget);
                    lease = null;
                    budgetOwned = false;
                    var deliveryStarted = Stopwatch.GetTimestamp();
                    await DeliverAsync(active, new DataMessage(block), job).ConfigureAwait(false);
                    var deliveryElapsed = Stopwatch.GetElapsedTime(deliveryStarted);
                    pipeline.RecordDeliveryWait(deliveryElapsed);
                    job.Telemetry.RecordFanoutWait(deliveryElapsed);
                    active.RemoveAll(worker => !worker.IsActive);
                    if (active.Count == 0)
                        return null;
                }
                finally
                {
                    lease?.Dispose();
                    if (budgetOwned)
                        bufferBudget.Release(readBufferSize);
                }
            }

            ValidateCompletedSourceRead(entry, totalRead);
            return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
        }
        finally
        {
            direct?.Dispose();
            if (buffered is not null)
                await buffered.DisposeAsync().ConfigureAwait(false);
        }
    }
'@
Replace-Exact $oldSequential $newSequential 'replace single-block source path with aligned/direct-capable path'

Replace-Exact @'
            long totalRead = 0;
            while (true)
            {
'@ @'
            long totalRead = 0;
            while (totalRead < entry.Size)
            {
'@ 'avoid redundant read past known source EOF'

Replace-Exact @'
                    lease = direct is null
                        ? SourceBufferLease.RentBuffered(readBufferSize)
                        : SourceBufferLease.RentAligned(readBufferSize, DirectIoSourceReader.MaximumSupportedAlignment);
'@ @'
                    // FAN-OUT payloads are always aligned, even when the source itself
                    // falls back to buffered I/O, so every capable destination can keep
                    // using Direct I/O without a whole-block staging copy.
                    lease = SourceBufferLease.RentAligned(
                        readBufferSize,
                        DirectIoSourceReader.MaximumSupportedAlignment);
'@ 'align buffered source payloads for direct destinations'

Replace-Exact @'
                                lease = SourceBufferLease.RentBuffered(readBufferSize);
                                read = await buffered.ReadAsync(lease.Memory, token).ConfigureAwait(false);
'@ @'
                                lease = SourceBufferLease.RentAligned(
                                    readBufferSize,
                                    DirectIoSourceReader.MaximumSupportedAlignment);
                                var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));
                                read = await buffered.ReadAsync(lease.Memory[..remaining], token).ConfigureAwait(false);
'@ 'keep fallback source payload aligned'

Replace-Exact @'
                        read = await buffered!.ReadAsync(lease.Memory, token).ConfigureAwait(false);
'@ @'
                        var remaining = checked((int)Math.Min(readBufferSize, entry.Size - totalRead));
                        read = await buffered!.ReadAsync(lease.Memory[..remaining], token).ConfigureAwait(false);
'@ 'bound buffered read to logical source length'

Replace-Exact @'
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
'@ 'fail early EOF instead of issuing redundant terminal reads'

Replace-Exact @'
        var writeThrough = entry.Size <= WriteThroughFileThreshold;
        var preallocationSize = StoragePreallocationPolicy.GetPreallocationSize(part, entry.Size, PreallocationThreshold);
        FileStream? stream = null;
        DirectIoDestinationWriter.Session? directSession = null;
        var preferDirect = !writeThrough && DirectIoDestinationWriter.IsEligible(worker.Device, entry.Size);
        if (preferDirect)
'@ @'
        var directRequested = DirectIoDestinationWriter.IsEligible(worker.Device, entry.Size);
        var writeThrough = !directRequested && entry.Size <= WriteThroughFileThreshold;
        var preallocationSize = StoragePreallocationPolicy.GetPreallocationSize(part, entry.Size, PreallocationThreshold);
        FileStream? stream = null;
        DirectIoDestinationWriter.Session? directSession = null;
        if (directRequested)
'@ 'let direct write outrank fixed small-file write-through policy'

Replace-Exact @'
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, directSession, writeThrough, preferDirect);
'@ @'
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, directSession, writeThrough, directRequested);
'@ 'remove obsolete preferDirect alias'

Replace-Exact @'
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    current.Entry.Size,
                    data.Length);
'@ @'
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.ExplorationQueueDepth,
                    data.Length);
'@ 'remove dead fileSize write-policy argument'

if ($text.Contains('SourcePrefetchThreshold')) { throw 'Fixed source prefetch threshold remains.' }
if ($text.Contains('ParallelFileThresholdBytes')) { throw 'Legacy parallel file threshold leaked into CopyEngine.' }
if ($text.Contains('preferDirect')) { throw 'Obsolete preferDirect alias remains.' }

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Performance unblock migration produced no changes.' }
git commit -m 'perf(core): remove fixed copy-path performance thresholds'
git push origin HEAD:main
