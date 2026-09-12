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
        var fileName = Path.GetFileName(path);
        var tmp = Path.Combine(parent, $".{fileName}.{token}.tmp");
        var backup = Path.Combine(parent, $".{fileName}.{token}.bak");
        var preserveBackup = false;

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

            var hadOld = File.Exists(path);
            if (hadOld)
            {
                WindowsPath.EnsureRegularFile(path, label);
                File.Move(path, backup);
            }

            try
            {
                File.Move(tmp, path);
                if (hadOld)
                    TryDelete(backup);
            }
            catch (Exception commitError)
            {
                if (!hadOld)
                    throw new IOException($"No se pudo finalizar {label}: {commitError.Message}", commitError);

                try
                {
                    File.Move(backup, path);
                    throw new IOException(
                        $"No se pudo reemplazar {label}: {commitError.Message}; se restauró la versión anterior",
                        commitError);
                }
                catch (IOException restoreError) when (File.Exists(backup))
                {
                    preserveBackup = true;
                    throw new IOException(
                        $"CRÍTICO: no se pudo reemplazar {label}: {commitError.Message}; tampoco restaurar {backup}: {restoreError.Message}",
                        new AggregateException(commitError, restoreError));
                }
            }
        }
        finally
        {
            TryDelete(tmp);
            if (!preserveBackup && File.Exists(path))
                TryDelete(backup);
        }
    }

    public static byte[] ReadRegularFile(string path, long maxBytes, string label)
    {
        WindowsPath.EnsureRegularFile(path, label);
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new IOException($"{label} es demasiado grande (máximo {maxBytes} bytes).");
        return File.ReadAllBytes(path);
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

public static class SessionStore
{
    private const string Magic = "RepartoCopierSession/1";
    private const long MaxSessionBytes = 1024 * 1024;

    public static string WithDefaultExtension(string path) =>
        Path.HasExtension(path) ? path : Path.ChangeExtension(path, ".repartocopy");

    public static void Save(string path, CopyPlan plan) =>
        AtomicStorage.Write(path, Encoding.UTF8.GetBytes(Render(plan)), "la copia guardada");

    public static CopyPlan Load(string path)
    {
        var bytes = AtomicStorage.ReadRegularFile(path, MaxSessionBytes, "la copia guardada");
        return Parse(Encoding.UTF8.GetString(bytes));
    }

    public static string Render(CopyPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Magic);
        builder.Append("source=").AppendLine(EncodeHex(plan.Source.Trim()));
        builder.Append("skip_same=").AppendLine(plan.SkipSame ? "1" : "0");
        builder.Append("keep_going=").AppendLine(plan.KeepGoing ? "1" : "0");
        foreach (var destination in plan.Destinations)
            builder.Append("dest=").AppendLine(EncodeHex(destination.Trim()));
        return builder.ToString();
    }

    public static CopyPlan Parse(string text)
    {
        using var reader = new StringReader(text);
        if (!string.Equals(reader.ReadLine(), Magic, StringComparison.Ordinal))
            throw new FormatException("Formato o versión de copia guardada no compatible.");

        string? source = null;
        bool? skipSame = null;
        bool? keepGoing = null;
        var destinations = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var separator = line.IndexOf('=');
            if (separator < 0)
                throw new FormatException("La copia guardada contiene una línea inválida.");
            var key = line[..separator];
            var value = line[(separator + 1)..];
            switch (key)
            {
                case "source":
                    if (source is not null) throw new FormatException("La copia guardada contiene más de un origen.");
                    source = DecodeHex(value, "source");
                    break;
                case "skip_same":
                    if (skipSame is not null) throw new FormatException("La copia guardada duplica skip_same.");
                    skipSame = ParseBool(value, "skip_same");
                    break;
                case "keep_going":
                    if (keepGoing is not null) throw new FormatException("La copia guardada duplica keep_going.");
                    keepGoing = ParseBool(value, "keep_going");
                    break;
                case "dest":
                    if (destinations.Count >= CopyPlan.MaxDestinations)
                        throw new FormatException($"La copia guardada supera el máximo de {CopyPlan.MaxDestinations} destinos.");
                    destinations.Add(DecodeHex(value, "dest"));
                    break;
                default:
                    throw new FormatException($"Campo de copia guardada desconocido: {key}.");
            }
        }

        if (source is null) throw new FormatException("La copia guardada no contiene source.");
        if (skipSame is null) throw new FormatException("La copia guardada no contiene skip_same.");
        if (keepGoing is null) throw new FormatException("La copia guardada no contiene keep_going.");
        return CopyPlan.Create(source, destinations, skipSame.Value, keepGoing.Value);
    }

    private static string EncodeHex(string value) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();

    private static string DecodeHex(string value, string field)
    {
        try
        {
            if ((value.Length & 1) != 0) throw new FormatException();
            return Encoding.UTF8.GetString(Convert.FromHexString(value));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            throw new FormatException($"Campo {field} tiene hexadecimal inválido.", ex);
        }
    }

    private static bool ParseBool(string value, string field) => value switch
    {
        "0" => false,
        "1" => true,
        _ => throw new FormatException($"Campo {field} inválido; se esperaba 0 o 1."),
    };
}
