using System.Text;

namespace RepartoCopier.Core;

/// <summary>
/// Cross-process ownership lease for a destination's recovery namespace.
/// The lock file is intentionally persistent; exclusivity is provided by the
/// open handle with FileShare.None, so a crashed process releases the lease.
/// </summary>
internal sealed class DestinationStateLease : IDisposable
{
    private FileStream? _stream;

    private DestinationStateLease(FileStream stream, string destinationRoot)
    {
        _stream = stream;
        DestinationRoot = destinationRoot;
    }

    internal string DestinationRoot { get; }

    internal static DestinationStateLease Acquire(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        StateLayout.PrepareStateDirectory(destinationRoot);
        var stateDirectory = StateLayout.StateDirectoryFor(destinationRoot);
        var lockPath = Path.Combine(stateDirectory, "active.lock");
        if (Directory.Exists(lockPath))
            throw new IOException($"El lock de estado no puede ser una carpeta: {lockPath}");
        if (File.Exists(lockPath))
            WindowsPath.EnsureRegularFile(lockPath, "El lock de estado");

        FileStream? stream = null;
        try
        {
            stream = new FileStream(lockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.WriteThrough,
            });
            stream.SetLength(0);
            var owner = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId};utc={DateTimeOffset.UtcNow:O}");
            stream.Write(owner);
            stream.Flush(flushToDisk: true);
            return new DestinationStateLease(stream, Path.GetFullPath(destinationRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw new IOException(
                $"El destino ya está siendo usado por otra operación o no se pudo bloquear su estado: {destinationRoot}",
                ex);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}