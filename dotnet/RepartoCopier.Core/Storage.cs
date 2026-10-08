using System.Text;
using Blake3;

namespace RepartoCopier.Core;

public static class AtomicStorage
{
    public static void Write(string path, ReadOnlySpan<byte> data, string label)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new IOException($"Ruta inválida para {label}: {path}");
        WindowsPath.EnsureNormalDirectory(parent, "La carpeta contenedora");
        if (File.Exists(path))
            WindowsPath.EnsureRegularFile(path, label);

        var token = $"{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Guid.NewGuid():N}";
        var tmp = Path.Combine(parent, $".{Path.GetFileName(path)}.{token}.tmp");

        try
        {
            using (var stream = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                WindowsPath.EnsureRegularFile(path, label);
            try
            {
                // One MoveFileEx(REPLACE_EXISTING) on the same volume: the old or the new file is always in
                // place, never neither, even if the process ends between steps.
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception commitError)
            {
                throw new IOException($"No se pudo guardar {label}: {commitError.Message}", commitError);
            }
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup; a successful commit must not be reported as failed
            // solely because a stale temporary/backup file could not be removed.
        }
    }
}

public static class StateLayout
{
    private const string StateDirectoryName = ".disk-duplicator-state";

    public static string StateDirectoryFor(string destinationRoot)
    {
        var parent = Path.GetDirectoryName(destinationRoot) ?? destinationRoot;
        return Path.Combine(parent, StateDirectoryName, StateId(destinationRoot));
    }

    public static string PrepareStateDirectory(string destinationRoot)
    {
        var state = StateDirectoryFor(destinationRoot);
        var container = Path.GetDirectoryName(state)
            ?? throw new IOException("La ruta del directorio de estado no tiene padre.");
        CreateNormalDirectory(container, "El contenedor de estado");
        CreateNormalDirectory(state, "El directorio de estado");
        return state;
    }

    public static string PrepareTempDirectory(string destinationRoot)
    {
        var tmp = Path.Combine(PrepareStateDirectory(destinationRoot), "tmp");
        CreateNormalDirectory(tmp, "El directorio temporal de estado");
        return tmp;
    }

    public static string JournalPath(string destinationRoot) =>
        Path.Combine(StateDirectoryFor(destinationRoot), "completed.jsonl");

    public static string ManifestPath(string destinationRoot) =>
        Path.Combine(StateDirectoryFor(destinationRoot), "manifest.b3");

    public static (string PartPath, string BackupPath) TransientPaths(
        string destinationRoot,
        string destinationFile)
    {
        var temp = Path.Combine(StateDirectoryFor(destinationRoot), "tmp");
        var id = TransientId(destinationFile);
        return (
            Path.Combine(temp, $"{id}.part"),
            Path.Combine(temp, $"{id}.bak"));
    }

    /// <summary><see cref="TransientPaths"/> for a temp directory already resolved by <see cref="PrepareTempDirectory"/>:
    /// the per-destination state id is hashed once per copy instead of once per file.</summary>
    internal static (string PartPath, string BackupPath) TransientPathsIn(string tempDirectory, string destinationFile)
    {
        var id = TransientId(destinationFile);
        return (Path.Combine(tempDirectory, $"{id}.part"), Path.Combine(tempDirectory, $"{id}.bak"));
    }

    public static string PartPath(string destinationRoot, string destinationFile) =>
        Path.Combine(StateDirectoryFor(destinationRoot), "tmp", $"{TransientId(destinationFile)}.part");

    public static string BackupPath(string destinationRoot, string destinationFile) =>
        Path.Combine(StateDirectoryFor(destinationRoot), "tmp", $"{TransientId(destinationFile)}.bak");

    public static string PersistedPathKey(string path) =>
        "p2:" + Convert.ToHexString(Encoding.Unicode.GetBytes(path)).ToLowerInvariant();

    public static string StateId(string path) => DigestPath(path, lowerCase: true, 32);
    public static string TransientId(string path) => DigestPath(path, lowerCase: true, 32);

    private static string DigestPath(string path, bool lowerCase, int hexChars)
    {
        var normalized = lowerCase ? path.ToLowerInvariant() : path;
        var bytes = Encoding.Unicode.GetBytes(normalized);
        var hash = Hasher.Hash(bytes).ToString();
        return hash[..hexChars];
    }

    private static void CreateNormalDirectory(string path, string label)
    {
        Directory.CreateDirectory(path);
        WindowsPath.EnsureNormalDirectory(path, label);
    }
}
