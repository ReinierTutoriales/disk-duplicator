using System.Diagnostics;
using Blake3;

namespace RepartoCopier.Core;

public sealed record SourceReaderMeasurement(
    int Reader,
    long Bytes,
    long DirectBytes,
    int CompletedPasses,
    TimeSpan WallElapsed,
    TimeSpan ReadTime,
    TimeSpan HashTime,
    string LastHash)
{
    public double MiBPerSecond => Bytes / 1048576d / Math.Max(WallElapsed.TotalSeconds, double.Epsilon);
}

public sealed record SourceReadBenchmarkResult(
    string Source,
    int Readers,
    int RequestedSeconds,
    TimeSpan Elapsed,
    double ProcessCpuPercent,
    IReadOnlyList<SourceReaderMeasurement> Measurements)
{
    public long TotalBytes => Measurements.Sum(item => item.Bytes);
    public double AggregateMiBPerSecond => TotalBytes / 1048576d / Math.Max(Elapsed.TotalSeconds, double.Epsilon);
}

/// <summary>Read-only capacity probe using the copy engine's Direct reader, 8 MiB
/// aligned blocks, sequential buffered fallback, and serial BLAKE3 per reader.
/// It has no destination writers, so its result is an upper bound for COPY.</summary>
public static class SourceReadBenchmark
{
    private const int BlockBytes = 8 * 1024 * 1024;

    public static async Task<SourceReadBenchmarkResult> RunAsync(
        string source, int readers, int seconds, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (readers is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(readers));
        if (seconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(seconds));
        var path = Path.GetFullPath(source);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < BlockBytes)
            throw new IOException("El origen de prueba debe ser un archivo existente de al menos 8 MiB.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("No se permiten enlaces simbólicos como origen de la prueba.");

        var length = info.Length;
        var modified = info.LastWriteTimeUtc;
        var device = StorageTopology.InspectDestination(Path.GetDirectoryName(path)!);
        var alignment = device.HasKnownSectorAlignment
            ? Math.Max(Environment.SystemPageSize, DirectIoSourceReader.RequiredAlignment(device))
            : Environment.SystemPageSize;
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0 || BlockBytes % alignment != 0)
            throw new IOException("La alineación del origen no admite bloques de 8 MiB.");

        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        var tasks = Enumerable.Range(1, readers)
            .Select(index => ReadAsync(index, path, length, modified, device, alignment, seconds, timer, token))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (results.Any(item => item.LastHash != results[0].LastHash))
            throw new IOException("Los lectores observaron contenido distinto en el origen.");
        timer.Stop();
        process.Refresh();
        var cpu = 100 * (process.TotalProcessorTime - cpuStart).TotalSeconds /
            Math.Max(timer.Elapsed.TotalSeconds * Environment.ProcessorCount, double.Epsilon);
        return new SourceReadBenchmarkResult(path, readers, seconds, timer.Elapsed, cpu, results);
    }

    private static async Task<SourceReaderMeasurement> ReadAsync(
        int index, string path, long length, DateTime modified, StorageDeviceInfo device,
        int alignment, int seconds, Stopwatch timer, CancellationToken token)
    {
        var readerStarted = Stopwatch.GetTimestamp();
        using var buffer = SourceBufferLease.RentAligned(BlockBytes, alignment);
        long bytes = 0, directBytes = 0, readTicks = 0, hashTicks = 0;
        var passes = 0;
        string lastHash = "";
        do
        {
            token.ThrowIfCancellationRequested();
            using var hasher = Hasher.New();
            DirectIoSourceReader.OverlappedSession? direct = null;
            FileStream? buffered = null;
            try
            {
                if (!DirectIoSourceReader.TryOpenOverlapped(path, device, BlockBytes, out direct))
                    buffered = OpenBuffered(path);
                long position = 0;
                while (position < length)
                {
                    token.ThrowIfCancellationRequested();
                    var started = Stopwatch.GetTimestamp();
                    int read;
                    if (direct is not null)
                    {
                        try
                        {
                            read = await direct.ReadAsync(buffer, BlockBytes, position, token).ConfigureAwait(false);
                            directBytes += read;
                        }
                        catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                        {
                            direct.Dispose();
                            direct = null;
                            buffered = OpenBuffered(path);
                            buffered.Position = position;
                            read = await buffered.ReadAsync(buffer.Memory[..(int)Math.Min(BlockBytes, length - position)], token).ConfigureAwait(false);
                        }
                    }
                    else
                        read = await buffered!.ReadAsync(buffer.Memory[..(int)Math.Min(BlockBytes, length - position)], token).ConfigureAwait(false);
                    readTicks += Stopwatch.GetElapsedTime(started).Ticks;
                    if (read <= 0 || position + read > length)
                        throw new IOException("La lectura del origen terminó antes de tiempo o cambió de tamaño.");
                    position += read;
                    bytes += read;
                    started = Stopwatch.GetTimestamp();
                    hasher.UpdateWithJoin(buffer.Memory.Span[..read]);
                    hashTicks += Stopwatch.GetElapsedTime(started).Ticks;
                }
            }
            finally
            {
                direct?.Dispose();
                if (buffered is not null) await buffered.DisposeAsync().ConfigureAwait(false);
            }
            var snapshot = new FileInfo(path);
            if (snapshot.Length != length || snapshot.LastWriteTimeUtc != modified)
                throw new IOException("El origen cambió durante la prueba.");
            lastHash = Convert.ToHexString(hasher.Finalize().AsSpan());
            passes++;
        } while (timer.Elapsed < TimeSpan.FromSeconds(seconds));
        return new SourceReaderMeasurement(index, bytes, directBytes, passes,
            Stopwatch.GetElapsedTime(readerStarted),
            TimeSpan.FromTicks(readTicks), TimeSpan.FromTicks(hashTicks), lastHash);
    }

    private static FileStream OpenBuffered(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 1,
    });
}
