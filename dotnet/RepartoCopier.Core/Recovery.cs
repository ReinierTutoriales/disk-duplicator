using System.Text;
using Blake3;

namespace RepartoCopier.Core;

internal sealed record RecoveryFile(
    string SourcePath,
    string RelativePath,
    long Size,
    long ModifiedUnixNanoseconds);

internal static class RecoveryManager
{
    private const int PreviousStateIdHex = 32;
    private const int LegacyStateIdHex = 16;
    private const int PreviousTransientIdHex = 32;
    private const int LegacyTransientIdHex = 24;

    internal static HashSet<string> PrepareAndNormalize(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
    {
        MigrateStateDirectory(destinationRoot);
        StateLayout.PrepareTempDirectory(destinationRoot);
        CleanupOwnedStaleFiles(destinationRoot, files);
        RecoverCompletedRewrite(destinationRoot);
        var valid = NormalizeCompletedState(sourceRoot, destinationRoot, files);
        CompactManifest(destinationRoot, files);
        return valid;
    }

    internal static string StateKey(RecoveryFile file) =>
        $"{StateLayout.PersistedPathKey(file.RelativePath)}|{file.Size}|{file.ModifiedUnixNanoseconds}";

    internal static string LegacyStateKey(RecoveryFile file)
    {
        var bytes = Encoding.UTF8.GetBytes(file.RelativePath);
        return $"{Convert.ToHexString(bytes).ToLowerInvariant()}|{file.Size}|{file.ModifiedUnixNanoseconds}";
    }

    internal static string ManifestKey(string relativePath) =>
        StateLayout.PersistedPathKey(relativePath);

    internal static string LegacyManifestKey(string relativePath) =>
        relativePath.Replace('\\', '/');

    internal static void AppendDurable(
        string destinationRoot,
        RecoveryFile file,
        ReadOnlySpan<byte> hash)
    {
        if (hash.Length != 32)
            throw new ArgumentException("BLAKE3 debe contener exactamente 32 bytes.", nameof(hash));

        StateLayout.PrepareStateDirectory(destinationRoot);
        var manifestLine = $"{Convert.ToHexString(hash).ToLowerInvariant()}  {ManifestKey(file.RelativePath)}{Environment.NewLine}";
        AppendAndFlush(StateLayout.ManifestPath(destinationRoot), manifestLine, "manifest");
        var journalLine = $"{{\"key\":\"{StateKey(file)}\"}}{Environment.NewLine}";
        AppendAndFlush(StateLayout.JournalPath(destinationRoot), journalLine, "state");
    }

    internal static Dictionary<string, byte[]> LoadManifestHashes(string destinationRoot)
    {
        var path = StateLayout.ManifestPath(destinationRoot);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return hashes;

        EnsureOwnedRegularFile(path, "manifest");
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            var hex = line[..separator];
            var name = line[(separator + 2)..];
            if (hex.Length != 64 || name.Length == 0)
                continue;
            try
            {
                var hash = Convert.FromHexString(hex);
                if (hash.Length == 32)
                    hashes[name] = hash;
            }
            catch (FormatException)
            {
                // Malformed manifest entries are never trusted for resume.
            }
        }
        return hashes;
    }

    internal static HashSet<string> LoadCompleted(string destinationRoot)
    {
        var path = StateLayout.JournalPath(destinationRoot);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return keys;

        EnsureOwnedRegularFile(path, "journal");
        foreach (var line in File.ReadLines(path))
        {
            const string marker = "\"key\":\"";
            var start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                continue;
            start += marker.Length;
            var end = line.IndexOf('"', start);
            if (end > start)
                keys.Add(line[start..end]);
        }
        return keys;
    }

    internal static void RewriteCompleted(string destinationRoot, HashSet<string> keys)
    {
        StateLayout.PrepareStateDirectory(destinationRoot);
        RecoverCompletedRewrite(destinationRoot);
        var path = StateLayout.JournalPath(destinationRoot);
        var tmp = StateRewriteTempPath(destinationRoot);
        var backup = StateRewriteBackupPath(destinationRoot);
        DeleteOwnedFileIfPresent(tmp, "temporal de estado");
        DeleteOwnedFileIfPresent(backup, "backup de estado");

        using (var stream = OpenNewDurable(tmp))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
        {
            foreach (var key in keys.OrderBy(value => value, StringComparer.Ordinal))
                writer.WriteLine($"{{\"key\":\"{key}\"}}");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        var hadOld = File.Exists(path);
        if (hadOld)
        {
            EnsureOwnedRegularFile(path, "journal");
            File.Move(path, backup);
        }

        try
        {
            File.Move(tmp, path);
            if (hadOld)
                File.Delete(backup);
        }
        catch (Exception commitError)
        {
            if (hadOld && File.Exists(backup))
            {
                try
                {
                    File.Move(backup, path);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"CRÍTICO: falló el commit del journal {path} y también su restauración desde {backup}.",
                        new AggregateException(commitError, restoreError));
                }
            }
            throw new IOException($"No se pudo actualizar el journal {path}.", commitError);
        }
    }

    internal static void RecoverCompletedRewrite(string destinationRoot)
    {
        var path = StateLayout.JournalPath(destinationRoot);
        var tmp = StateRewriteTempPath(destinationRoot);
        var backup = StateRewriteBackupPath(destinationRoot);

        if (File.Exists(path) || Directory.Exists(path))
        {
            EnsureOwnedRegularFile(path, "journal");
            DeleteOwnedFileIfPresent(backup, "backup de estado");
            DeleteOwnedFileIfPresent(tmp, "temporal de estado");
            return;
        }

        if (File.Exists(backup) || Directory.Exists(backup))
        {
            EnsureOwnedRegularFile(backup, "backup de estado");
            File.Move(backup, path);
        }
        DeleteOwnedFileIfPresent(tmp, "temporal de estado");
    }

    internal static void CompactManifest(
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
    {
        var path = StateLayout.ManifestPath(destinationRoot);
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        EnsureOwnedRegularFile(path, "manifest");

        var hashes = LoadManifestHashes(destinationRoot);
        var entries = new List<(string Key, byte[] Hash)>();
        foreach (var file in files)
        {
            var current = ManifestKey(file.RelativePath);
            if (hashes.TryGetValue(current, out var hash)
                || hashes.TryGetValue(LegacyManifestKey(file.RelativePath), out hash))
            {
                entries.Add((current, hash));
            }
        }
        entries.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));

        var tmp = Path.ChangeExtension(path, "b3.compact");
        var backup = Path.ChangeExtension(path, "b3.compact.bak");
        DeleteOwnedFileIfPresent(tmp, "temporal de manifest");
        DeleteOwnedFileIfPresent(backup, "backup de manifest");

        using (var stream = OpenNewDurable(tmp))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
        {
            foreach (var (key, hash) in entries)
                writer.WriteLine($"{Convert.ToHexString(hash).ToLowerInvariant()}  {key}");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(path, backup);
        try
        {
            File.Move(tmp, path);
            File.Delete(backup);
        }
        catch (Exception commitError)
        {
            try
            {
                File.Move(backup, path);
            }
            catch (Exception restoreError)
            {
                throw new IOException(
                    $"CRÍTICO: falló la compactación de {path} y su restauración.",
                    new AggregateException(commitError, restoreError));
            }
            throw new IOException($"No se pudo compactar {path}; el manifest anterior fue restaurado.", commitError);
        }
    }

    private static HashSet<string> NormalizeCompletedState(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
    {
        var loaded = LoadCompleted(destinationRoot);
        if (loaded.Count == 0)
            return loaded;

        var manifest = LoadManifestHashes(destinationRoot);
        var valid = new HashSet<string>(StringComparer.Ordinal);
        var sourceHashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var currentKey = StateKey(file);
            var oldKey = LegacyStateKey(file);
            if (!loaded.Contains(currentKey) && !loaded.Contains(oldKey))
                continue;

            if (!manifest.TryGetValue(ManifestKey(file.RelativePath), out var expected)
                && !manifest.TryGetValue(LegacyManifestKey(file.RelativePath), out expected))
                continue;

            if (!sourceHashes.TryGetValue(file.RelativePath, out var sourceHash))
            {
                sourceHash = HashFile(file.SourcePath);
                sourceHashes[file.RelativePath] = sourceHash;
            }
            if (!sourceHash.AsSpan().SequenceEqual(expected))
                continue;

            var destination = Path.Combine(destinationRoot, file.RelativePath);
            if (!File.Exists(destination) || Directory.Exists(destination))
                continue;
            if (WindowsPath.IsReparsePoint(destination))
                continue;
            if (new FileInfo(destination).Length != file.Size)
                continue;
            var destinationHash = HashFile(destination);
            if (destinationHash.AsSpan().SequenceEqual(expected))
                valid.Add(currentKey);
        }

        if (!valid.SetEquals(loaded))
            RewriteCompleted(destinationRoot, valid);
        return valid;
    }

    private static void CleanupOwnedStaleFiles(
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
    {
        foreach (var file in files)
        {
            var destination = Path.Combine(destinationRoot, file.RelativePath);
            foreach (var part in PartCandidates(destinationRoot, destination))
                DeleteOwnedFileIfPresent(part, "temporal");

            foreach (var backup in BackupCandidates(destinationRoot, destination))
            {
                if (!File.Exists(backup) && !Directory.Exists(backup))
                    continue;
                EnsureOwnedRegularFile(backup, "backup de copia");
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    if (Directory.Exists(destination))
                        throw new IOException($"No se puede limpiar backup porque el destino es una carpeta: {destination}");
                    File.Delete(backup);
                }
                else
                {
                    var parent = Path.GetDirectoryName(destination)
                        ?? throw new IOException($"Destino inválido: {destination}");
                    Directory.CreateDirectory(parent);
                    File.Move(backup, destination);
                }
            }
        }
    }

    private static IEnumerable<string> PartCandidates(string root, string destination)
    {
        yield return StateLayout.PartPath(root, destination);
        yield return Path.Combine(StateLayout.StateDirectoryFor(root), "tmp", $"{PreviousTransientId(destination)}.part");
        yield return Path.Combine(StateLayout.StateDirectoryFor(root), "tmp", $"{LegacyTransientId(destination)}.part");
    }

    private static IEnumerable<string> BackupCandidates(string root, string destination)
    {
        yield return StateLayout.BackupPath(root, destination);
        yield return Path.Combine(StateLayout.StateDirectoryFor(root), "tmp", $"{PreviousTransientId(destination)}.bak");
        yield return Path.Combine(StateLayout.StateDirectoryFor(root), "tmp", $"{LegacyTransientId(destination)}.bak");
    }

    private static void MigrateStateDirectory(string destinationRoot)
    {
        var current = StateLayout.StateDirectoryFor(destinationRoot);
        var container = Path.GetDirectoryName(current)
            ?? throw new IOException("La ruta del directorio de estado no tiene padre.");
        Directory.CreateDirectory(container);
        WindowsPath.EnsureNormalDirectory(container, "El contenedor de estado");

        if (Directory.Exists(current) || File.Exists(current))
        {
            WindowsPath.EnsureNormalDirectory(current, "El directorio de estado");
            return;
        }

        var parent = Path.GetDirectoryName(destinationRoot) ?? destinationRoot;
        var candidates = new[]
        {
            Path.Combine(parent, ".disk-duplicator-state", PreviousStateId(destinationRoot)),
            Path.Combine(parent, ".disk-duplicator-state", LegacyStateId(destinationRoot)),
        };
        foreach (var previous in candidates)
        {
            if (WindowsPath.SamePath(previous, current) || (!Directory.Exists(previous) && !File.Exists(previous)))
                continue;
            WindowsPath.EnsureNormalDirectory(previous, "El directorio de estado anterior");
            Directory.Move(previous, current);
            break;
        }
    }

    private static string StateRewriteTempPath(string destinationRoot) =>
        Path.Combine(StateLayout.StateDirectoryFor(destinationRoot), "completed.jsonl.preflight");

    private static string StateRewriteBackupPath(string destinationRoot) =>
        Path.Combine(StateLayout.StateDirectoryFor(destinationRoot), "completed.jsonl.preflight.bak");

    private static string PreviousStateId(string path) =>
        DigestHex(Encoding.Unicode.GetBytes(path), PreviousStateIdHex);

    private static string PreviousTransientId(string path) =>
        DigestHex(Encoding.Unicode.GetBytes(path), PreviousTransientIdHex);

    private static string LegacyStateId(string path) =>
        DigestHex(Encoding.UTF8.GetBytes(path), LegacyStateIdHex);

    private static string LegacyTransientId(string path) =>
        DigestHex(Encoding.UTF8.GetBytes(path), LegacyTransientIdHex);

    private static string DigestHex(ReadOnlySpan<byte> bytes, int chars)
    {
        var hex = Hasher.Hash(bytes).ToString();
        return hex[..chars];
    }

    private static byte[] HashFile(string path)
    {
        WindowsPath.EnsureRegularFile(path, "El archivo para verificación");
        using var hasher = Hasher.New();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[4 * 1024 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            hasher.Update(buffer.AsSpan(0, read));
        }
        return hasher.Finalize().AsSpan().ToArray();
    }

    private static void AppendAndFlush(string path, string line, string label)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new IOException($"Ruta inválida de {label}: {path}");
        Directory.CreateDirectory(parent);
        WindowsPath.EnsureNormalDirectory(parent, $"La carpeta de {label}");
        if (File.Exists(path) || Directory.Exists(path))
            EnsureOwnedRegularFile(path, label);

        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.WriteThrough);
        var bytes = Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenNewDurable(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);

    private static void EnsureOwnedRegularFile(string path, string label)
    {
        if (Directory.Exists(path) || !File.Exists(path) || WindowsPath.IsReparsePoint(path))
            throw new IOException(
                $"Entrada de estado no segura para {label}: {path}. Se esperaba un archivo regular sin enlaces ni reparse points.");
    }

    private static void DeleteOwnedFileIfPresent(string path, string label)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        EnsureOwnedRegularFile(path, label);
        File.Delete(path);
    }
}

