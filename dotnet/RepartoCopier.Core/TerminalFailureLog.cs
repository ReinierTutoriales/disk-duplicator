using System.Reflection;
using System.Text;

namespace RepartoCopier.Core;

internal static class TerminalFailureLog
{
    internal const string FileName = "terminal-error.log";
    private const string AppDirectoryName = "RepartoCopier";
    private static Func<string>? _localLogDirectoryOverride;

    internal static void Write(string destinationRoot, string message)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot) || string.IsNullOrWhiteSpace(message))
            return;

        string normalizedRoot;
        try { normalizedRoot = Path.GetFullPath(destinationRoot); }
        catch { normalizedRoot = destinationRoot; }

        var payload = BuildPayload(normalizedRoot, message);
        WriteLocalFirst(payload);
        QueueDestinationCopy(normalizedRoot, payload);
    }

    internal static string LocalLogPath() =>
        Path.Combine(LocalLogDirectory(), FileName);

    internal static void SetLocalLogDirectoryForTests(string? directory)
    {
        _localLogDirectoryOverride = directory is null ? null : () => directory;
    }

    internal static string BuildPayload(string destinationRoot, string message) =>
        $"utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
        $"head_sha={HeadSha()}{Environment.NewLine}" +
        $"destination={destinationRoot}{Environment.NewLine}" +
        $"preallocation={StoragePreallocationPolicy.DiagnosticState}{Environment.NewLine}" +
        $"{message}{Environment.NewLine}{Environment.NewLine}";

    private static void WriteLocalFirst(string payload)
    {
        try
        {
            var localDirectory = LocalLogDirectory();
            Directory.CreateDirectory(localDirectory);
            var logPath = Path.Combine(localDirectory, FileName);
            WriteThrough(logPath, payload, ensureRegularFile: false);
        }
        catch
        {
            // Best-effort diagnostic channel. Never replace the real terminal copy failure.
        }
    }

    private static void QueueDestinationCopy(string destinationRoot, string payload)
    {
        _ = Task.Run(() => WriteDestinationBestEffort(destinationRoot, payload));
    }

    private static void WriteDestinationBestEffort(string normalizedRoot, string payload)
    {
        try
        {
            var stateDirectory = StateLayout.StateDirectoryFor(normalizedRoot);
            Directory.CreateDirectory(stateDirectory);
            WindowsPath.EnsureNormalDirectory(stateDirectory, "El directorio de diagnóstico terminal");
            var logPath = Path.Combine(stateDirectory, FileName);
            if (Directory.Exists(logPath))
                return;
            if (File.Exists(logPath))
                WindowsPath.EnsureRegularFile(logPath, "El log de diagnóstico terminal");

            WriteThrough(logPath, payload, ensureRegularFile: true);
        }
        catch
        {
            // Best-effort destination copy. The local sink above is the authoritative fallback.
        }
    }

    private static void WriteThrough(string path, string payload, bool ensureRegularFile)
    {
        if (ensureRegularFile && File.Exists(path))
            WindowsPath.EnsureRegularFile(path, "El log de diagnóstico terminal");

        var bytes = Encoding.UTF8.GetBytes(payload);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 1,
            Options = FileOptions.WriteThrough,
        });
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string LocalLogDirectory()
    {
        var overrideDirectory = _localLogDirectoryOverride?.Invoke();
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return overrideDirectory;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData;
        return Path.Combine(root, AppDirectoryName);
    }

    private static string HeadSha()
    {
        var informationalVersion = typeof(TerminalFailureLog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plus = informationalVersion.LastIndexOf('+');
            if (plus >= 0 && plus < informationalVersion.Length - 1)
                return informationalVersion[(plus + 1)..];
        }

        return "unknown";
    }
}