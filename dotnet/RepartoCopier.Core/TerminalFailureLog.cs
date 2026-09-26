using System.Text;

namespace RepartoCopier.Core;

internal static class TerminalFailureLog
{
    private const string FileName = "terminal-error.log";

    internal static void Write(string destinationRoot, string message)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot) || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            var normalizedRoot = Path.GetFullPath(destinationRoot);
            var stateDirectory = StateLayout.StateDirectoryFor(normalizedRoot);
            Directory.CreateDirectory(stateDirectory);
            WindowsPath.EnsureNormalDirectory(stateDirectory, "El directorio de diagnóstico terminal");
            var logPath = Path.Combine(stateDirectory, FileName);
            if (Directory.Exists(logPath))
                return;
            if (File.Exists(logPath))
                WindowsPath.EnsureRegularFile(logPath, "El log de diagnóstico terminal");

            var payload = Encoding.UTF8.GetBytes(
                $"utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}");
            using var stream = new FileStream(logPath, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 1,
                Options = FileOptions.WriteThrough,
            });
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            // Best-effort diagnostic channel. Never replace the real terminal copy failure.
        }
    }
}
