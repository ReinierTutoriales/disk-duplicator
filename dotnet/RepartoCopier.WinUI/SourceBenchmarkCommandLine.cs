namespace RepartoCopier.WinUI;

internal sealed record SourceBenchmarkCommandLine(
    string Source, string Report, int[] Readers, int Seconds)
{
    internal static SourceBenchmarkCommandLine Parse(string[] args)
    {
        if (args.Length is not (3 or 5 or 7) || args[0] != "--source-bench")
            throw new ArgumentException("Uso: --source-bench <archivo> <informe.json> [--readers 1,2,4] [--seconds 20]");
        var readers = new[] { 1, 2, 4 };
        var seconds = 20;
        var seenReaders = false;
        var seenSeconds = false;
        for (var index = 3; index < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--readers" when !seenReaders:
                    var parts = args[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length is < 1 or > 3 ||
                        parts.Any(part => !int.TryParse(part, out var value) || value is < 1 or > 4) ||
                        parts.Select(int.Parse).Distinct().Count() != parts.Length)
                        throw new ArgumentException("--readers debe contener de uno a tres valores distintos entre 1 y 4.");
                    readers = parts.Select(int.Parse).ToArray();
                    seenReaders = true;
                    break;
                case "--seconds" when !seenSeconds:
                    if (!int.TryParse(args[index + 1], out seconds) || seconds is < 1 or > 300)
                        throw new ArgumentException("--seconds debe estar entre 1 y 300.");
                    seenSeconds = true;
                    break;
                default:
                    throw new ArgumentException($"Opción desconocida o repetida: {args[index]}");
            }
        }
        if (Path.GetFullPath(args[1]).Equals(Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("El informe no puede usar la ruta del archivo de origen.");
        return new SourceBenchmarkCommandLine(args[1], args[2], readers, seconds);
    }
}
