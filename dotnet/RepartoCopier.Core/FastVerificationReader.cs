using System.Diagnostics;

namespace RepartoCopier.Core;

internal static class FastVerificationReader
{
    private const int DirectThreshold = 4 * 1024 * 1024;
    private const int DirectProbeSize = 8 * 1024 * 1024;

    internal static async Task<bool> VerifyAsync(
        string path,
        StorageDeviceInfo device,
        DeviceScheduler scheduler,
        VerificationPlan plan,
        CopyJob job,
        DestinationProgress progress)
    {
        if (plan.Blocks.Count == 0)
            return plan.Length == 0;

        if (plan.Length >= DirectThreshold &&
            DirectIoSourceReader.TryOpenOverlappedForVerification(path, device, DirectProbeSize, out var direct))
        {
            using (direct)
            {
                try
                {
                    return await VerifyDirectAsync(path, device, scheduler, plan, direct!, job, progress).ConfigureAwait(false);
                }
                catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                {
                    // Unsupported unbuffered/overlapped combinations fall back to the
                    // portable asynchronous reader. Hardware faults remain fatal.
                }
            }
        }

        return await VerifyBufferedAsync(path, plan, scheduler, job, progress).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyDirectAsync(
        string path,
        StorageDeviceInfo device,
        DeviceScheduler scheduler,
        VerificationPlan plan,
        DirectIoSourceReader.OverlappedSession session,
        CopyJob job,
        DestinationProgress progress)
    {
        var depth = device.MediaKind == StorageMediaKind.SolidState
            ? Math.Clamp(scheduler.MaxOutstandingIo, 1, 2)
            : 1;
        var pending = new Queue<PendingRead>();
        long offset = 0;
        var index = 0;

        while (index < plan.Blocks.Count || pending.Count > 0)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            while (index < plan.Blocks.Count && pending.Count < depth)
            {
                var expected = plan.Blocks[index++];
                var requestSize = AlignUp(expected.Length, session.Alignment);
                var lease = SourceBufferLease.RentAligned(requestSize, session.Alignment);
                var started = Stopwatch.GetTimestamp();
                var task = ReadDirectAsync(session, scheduler, lease, requestSize, offset, job.Token);
                pending.Enqueue(new PendingRead(offset, expected, lease, started, task));
                offset = checked(offset + expected.Length);
            }

            var current = pending.Dequeue();
            try
            {
                var read = await current.Read.ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(current.Started);
                job.Telemetry.RecordVerifyRead(current.Expected.Length, elapsed);
                if (read < current.Expected.Length)
                    throw new IOException($"Lectura incompleta durante verificación: {path}");

                var crcStarted = Stopwatch.GetTimestamp();
                var actual = FastCrc32.Compute(current.Buffer.Memory.Span[..current.Expected.Length]);
                job.Telemetry.RecordVerifyHash(current.Expected.Length, Stopwatch.GetElapsedTime(crcStarted));
                if (actual != current.Expected.Crc32)
                    return false;
                progress.AddVerified(current.Expected.Length);
            }
            finally
            {
                current.Buffer.Dispose();
            }
        }

        return offset == plan.Length;
    }

    private static async Task<int> ReadDirectAsync(
        DirectIoSourceReader.OverlappedSession session,
        DeviceScheduler scheduler,
        SourceBufferLease buffer,
        int requestSize,
        long offset,
        CancellationToken token)
    {
        using var io = await scheduler.AcquireIoAsync(token).ConfigureAwait(false);
        return await session.ReadAsync(buffer, requestSize, offset, token).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyBufferedAsync(
        string path,
        VerificationPlan plan,
        DeviceScheduler scheduler,
        CopyJob job,
        DestinationProgress progress)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long total = 0;
        foreach (var expected in plan.Blocks)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
            using var buffer = SourceBufferLease.RentBuffered(expected.Length);
            var filled = 0;
            var started = Stopwatch.GetTimestamp();
            using (var io = await scheduler.AcquireIoAsync(job.Token).ConfigureAwait(false))
            {
                while (filled < expected.Length)
                {
                    var read = await stream.ReadAsync(buffer.Memory[filled..expected.Length], job.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    filled += read;
                }
            }
            job.Telemetry.RecordVerifyRead(filled, Stopwatch.GetElapsedTime(started));
            if (filled != expected.Length)
                return false;

            var crcStarted = Stopwatch.GetTimestamp();
            var actual = FastCrc32.Compute(buffer.Memory.Span[..filled]);
            job.Telemetry.RecordVerifyHash(filled, Stopwatch.GetElapsedTime(crcStarted));
            if (actual != expected.Crc32)
                return false;
            progress.AddVerified(filled);
            total = checked(total + filled);
        }
        return total == plan.Length && stream.Position == plan.Length;
    }

    private static int AlignUp(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private sealed record PendingRead(
        long Offset,
        VerificationBlock Expected,
        SourceBufferLease Buffer,
        long Started,
        Task<int> Read);
}
