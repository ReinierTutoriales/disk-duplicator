namespace RepartoCopier.Core;

public sealed record CopyPlan(
    string Source,
    IReadOnlyList<string> Destinations,
    bool SkipSame,
    bool KeepGoing)
{
    public const int MaxDestinations = 256;

    public static CopyPlan Create(
        string source,
        IEnumerable<string> destinations,
        bool skipSame,
        bool keepGoing)
    {
        source = (source ?? string.Empty).Trim();
        if (source.Length == 0)
            throw new ArgumentException("Selecciona un archivo o carpeta de origen.", nameof(source));

        var clean = new List<string>();
        foreach (var raw in destinations ?? [])
        {
            if (clean.Count >= MaxDestinations)
                throw new ArgumentException($"La copia supera el máximo de {MaxDestinations} destinos.", nameof(destinations));

            var destination = (raw ?? string.Empty).Trim();
            if (destination.Length == 0)
                throw new ArgumentException("La copia contiene un destino vacío.", nameof(destinations));
            if (WindowsPath.SamePath(source, destination))
                throw new ArgumentException("El origen no puede ser también un destino.", nameof(destinations));
            if (clean.Any(current => WindowsPath.SamePath(current, destination)))
                throw new ArgumentException("La copia contiene destinos duplicados.", nameof(destinations));

            clean.Add(destination);
        }

        if (clean.Count == 0)
            throw new ArgumentException("Agrega al menos un destino.", nameof(destinations));

        return new CopyPlan(source, clean, skipSame, keepGoing);
    }

    public static int AppendUniqueDestinations(
        string source,
        IList<string> existing,
        IEnumerable<string> selected)
    {
        var added = 0;
        foreach (var raw in selected)
        {
            if (existing.Count >= MaxDestinations)
                break;

            var path = (raw ?? string.Empty).Trim();
            if (path.Length == 0 || WindowsPath.SamePath(path, source))
                continue;
            if (existing.Any(current => WindowsPath.SamePath(current, path)))
                continue;

            existing.Add(path);
            added++;
        }
        return added;
    }
}

public static class WindowsPath
{
    public static string NormalizeIdentity(string path)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        value = value.Replace('/', '\\');
        var trimmed = value.TrimEnd('\\');
        if (trimmed.Length == 0)
            return value;
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed + "\\";
        return trimmed;
    }

    public static bool SamePath(string left, string right) =>
        string.Equals(
            NormalizeIdentity(left),
            NormalizeIdentity(right),
            StringComparison.OrdinalIgnoreCase);

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"No se pudo validar de forma segura la ruta: {path}", ex);
        }
    }

    public static void EnsureNormalDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
            throw new IOException($"{label} no existe: {path}");
        if (IsReparsePoint(path))
            throw new IOException($"{label} es un enlace/junction/reparse point: {path}");
    }

    public static void EnsureRegularFile(string path, string label)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            throw new IOException($"{label} no es un archivo regular seguro: {path}");
        if (IsReparsePoint(path))
            throw new IOException($"{label} es un enlace/junction/reparse point: {path}");
    }
}
