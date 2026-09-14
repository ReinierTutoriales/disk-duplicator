$ErrorActionPreference = 'Stop'

$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)

$oldWriteBody = @'
                var queueDepth = StorageWritePolicy.BufferedLargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.MaxOutstandingIo,
                    current.Entry.Size,
                    data.Length);
                var started = Stopwatch.GetTimestamp();

                if (queueDepth >= 2)
                {
                    await WriteQueueDepthTwoAsync(worker, current, data, job).ConfigureAwait(false);
                }
                else
                {
                    using var ioLease = await worker.DeviceScheduler.AcquireIoAsync(job.Token).ConfigureAwait(false);
                    await ExplicitOffsetWriter.WriteOneAsync(
                        current.Stream.SafeFileHandle,
                        data,
                        current.Copied,
                        job.Token).ConfigureAwait(false);
                    job.Telemetry.RecordWriteOperation();
                }
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started), current.WriteThrough);
'@

$newWriteBody = @'
                var queueDepth = StorageWritePolicy.LargeWriteQueueDepth(
                    worker.Device,
                    worker.DeviceScheduler.MaxOutstandingIo,
                    current.Entry.Size,
                    data.Length);
                var started = Stopwatch.GetTimestamp();
                var operations = await DestinationWriteCoordinator.WriteAsync(
                    current.Stream.SafeFileHandle,
                    data,
                    current.Copied,
                    queueDepth,
                    StorageWritePolicy.MinimumParallelSliceBytes,
                    worker.DeviceScheduler,
                    job.Token).ConfigureAwait(false);
                for (var operation = 0; operation < operations; operation++)
                    job.Telemetry.RecordWriteOperation();
                job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started), current.WriteThrough);
'@

$oldQd2Method = @'
    private static async Task WriteQueueDepthTwoAsync(
        DestinationWorker worker,
        CurrentFile current,
        ReadOnlyMemory<byte> data,
        CopyJob job)
    {
        var stream = current.Stream
            ?? throw new InvalidOperationException("El .part no está abierto para escritura QD2.");
        var firstLength = data.Length / 2;
        var secondLength = data.Length - firstLength;
        if (firstLength < StorageWritePolicy.MinimumParallelSliceBytes ||
            secondLength < StorageWritePolicy.MinimumParallelSliceBytes)
        {
            using var fallbackLease = await worker.DeviceScheduler.AcquireIoAsync(job.Token).ConfigureAwait(false);
            await ExplicitOffsetWriter.WriteOneAsync(
                stream.SafeFileHandle,
                data,
                current.Copied,
                job.Token).ConfigureAwait(false);
            job.Telemetry.RecordWriteOperation();
            return;
        }

        using var pairLease = await worker.DeviceScheduler.AcquireIoPairAsync(job.Token).ConfigureAwait(false);

        var firstOffset = current.Copied;
        var secondOffset = checked(firstOffset + firstLength);
        var handle = stream.SafeFileHandle;

        await ExplicitOffsetWriter.WriteTwoAsync(
            handle,
            data[..firstLength],
            firstOffset,
            data.Slice(firstLength, secondLength),
            secondOffset,
            job.Token).ConfigureAwait(false);
        job.Telemetry.RecordWriteOperation();
        job.Telemetry.RecordWriteOperation();
    }

'@

if (-not $text.Contains($oldWriteBody)) {
    throw 'CopyEngine writer body no coincide exactamente; migración abortada sin escribir.'
}
if (-not $text.Contains($oldQd2Method)) {
    throw 'WriteQueueDepthTwoAsync no coincide exactamente; migración abortada sin escribir.'
}

$text = $text.Replace($oldWriteBody, $newWriteBody)
$text = $text.Replace($oldQd2Method, '')

if ($text.Contains('WriteQueueDepthTwoAsync') -or $text.Contains('AcquireIoPairAsync') -or $text.Contains('WriteTwoAsync')) {
    throw 'Quedó una ruta QD2 antigua en CopyEngine después de la migración.'
}
if (-not $text.Contains('DestinationWriteCoordinator.WriteAsync')) {
    throw 'El consumidor de producción del coordinador variable no quedó conectado.'
}

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) {
    throw 'La migración no produjo cambios.'
}
git commit -m 'perf(core): migrate writer to variable queue depth'
git push origin HEAD:main
