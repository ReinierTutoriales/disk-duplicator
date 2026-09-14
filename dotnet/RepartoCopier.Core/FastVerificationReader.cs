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
        VerificationReadBudget readBudget,
        CopyJob job,
        DestinationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(readBudget);
        if (plan.Blocks.Count == 0)
            return plan.Length == 0;

        if (plan.Length >= DirectThreshold &&
            DirectIoSourceReader.TryOpenOverlappedForVerification(path, device, DirectProbeSize, out var direct))
        {
            using (direct)
            {
                try
                {
                    return await VerifyDirectAsync(path, scheduler, plan, readBudget, direct!, job, progress).ConfigureAwait(false);
                }
                catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                {
                    // Unsupported unbuffered/overlapped combinations fall back to the
                    // portable asynchronous reader. Hardware faults remain fatal.
                }
            }
        }

        return await VerifyBufferedAsync(path, plan, scheduler, readBudget, job, progress).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyDirectAsync(
        string path,
        DeviceScheduler scheduler,
        VerificationPlan plan,
        VerificationReadBudget readBudget,
        DirectIoSourceReader.OverlappedSession session,
        CopyJob job,
        DestinationProgress progress)
    {
        // DeviceScheduler is the physical-I/O authority. There is deliberately no
        // verification-specific QD2/QD16 cap here. The global byte budget below is
        // the memory authority shared by every destination verifier.
        var depth = Math.Max(1, scheduler.MaxOutstandingIo);
        var pending = new Queue<PendingRead>();
        long offset = 0;
        var index = 0;

        try
        {
            while (index < plan.Blocks.Count || pending.Count > 0)
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

                while (index < plan.Blocks.Count && pending.Count < depth)
                {
                    var expected = plan.Blocks[index++];
                    var requestSize = AlignUp(expected.Length, session.Alignment);
                    var reservation = await readBudget.AcquireAsync(requestSize, job.Token).ConfigureAwait(false);
                    SourceBufferLease? lease = null;
                    try
                    {
                        lease = SourceBufferLease.RentAligned(requestSize, session.Alignment);
                        var started = Stopwatch.GetTimestamp();
                        var task = ReadDirectAsync(session, scheduler, lease, requestSize, offset, job.Token);
                        pending.Enqueue(new PendingRead(expected, lease, reservation, started, task));
                        lease = null;
                        reservation = null!;
                        offset = checked(offset + expected.Length);
                    }
                    finally
                    {
                        lease?.Dispose();
                        reservation?.Dispose();
                    }
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
                    current.Reservation.Dispose();
                }
            }

            return offset == plan.Length;
        }
        finally
        {
            await DrainPendingAsync(pending).ConfigureAwait(false);
        }
    }

    private static async Task DrainPendingAsync(Queue<PendingRead> pending)
    {
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            try { await current.Read.ConfigureAwait(false); }
            catch { }
            finally
            {
                current.Buffer.Dispose();
                current.Reservation.Dispose();
            }
        }
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
        VerificationReadBudget readBudget,
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
            using var reservation = await readBudget.AcquireAsync(expected.Length, job.Token).ConfigureAwait(false);
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
        VerificationBlock Expected,
        SourceBufferLease Buffer,
        VerificationReadBudget.Lease Reservation,
        long Started,
        Task<int> Read);
}
