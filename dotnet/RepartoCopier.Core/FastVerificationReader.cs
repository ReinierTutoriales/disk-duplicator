using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

internal static class FastVerificationReader
{
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

        if (DirectIoSourceReader.TryOpenOverlappedForVerification(
                path,
                device,
                DirectIoSourceReader.MaximumSupportedAlignment,
                out var direct))
        {
            using (direct)
            {
                try
                {
                    return await VerifyDirectAsync(path, scheduler, plan, readBudget, direct!, job, progress).ConfigureAwait(false);
                }
                catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                {
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
        var pending = new Queue<PendingRead>();
        long offset = 0;
        var index = 0;

        try
        {
            while (index < plan.Blocks.Count || pending.Count > 0)
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

                var depth = Math.Max(1, scheduler.ExplorationQueueDepth);
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

    private static async Task<bool> VerifyBufferedAsync(
        string path,
        VerificationPlan plan,
        DeviceScheduler scheduler,
        VerificationReadBudget readBudget,
        CopyJob job,
        DestinationProgress progress)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var pending = new Queue<PendingRead>();
        long offset = 0;
        var index = 0;

        try
        {
            while (index < plan.Blocks.Count || pending.Count > 0)
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

                var depth = Math.Max(1, scheduler.ExplorationQueueDepth);
                while (index < plan.Blocks.Count && pending.Count < depth)
                {
                    var expected = plan.Blocks[index++];
                    var reservation = await readBudget.AcquireAsync(expected.Length, job.Token).ConfigureAwait(false);
                    SourceBufferLease? lease = null;
                    try
                    {
                        lease = SourceBufferLease.RentBuffered(expected.Length);
                        var started = Stopwatch.GetTimestamp();
                        var task = ReadBufferedAsync(
                            handle,
                            scheduler,
                            lease,
                            expected.Length,
                            offset,
                            job.Token);
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
                    job.Telemetry.RecordVerifyRead(current.Expected.Length, Stopwatch.GetElapsedTime(current.Started));
                    if (read != current.Expected.Length)
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
        using var io = await scheduler.AcquireIoAsync(requestSize, token).ConfigureAwait(false);
        return await session.ReadAsync(buffer, requestSize, offset, token).ConfigureAwait(false);
    }

    private static async Task<int> ReadBufferedAsync(
        SafeFileHandle handle,
        DeviceScheduler scheduler,
        SourceBufferLease buffer,
        int requestSize,
        long offset,
        CancellationToken token)
    {
        using var io = await scheduler.AcquireIoAsync(requestSize, token).ConfigureAwait(false);
        return await RandomAccess.ReadAsync(handle, buffer.Memory[..requestSize], offset, token).ConfigureAwait(false);
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
