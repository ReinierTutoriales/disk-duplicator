namespace RepartoCopier.Core;

/// <summary>
/// Cleanup that happens only after the namespace commit is already valid.
/// A stale backup is recoverable housekeeping and must not turn a successful
/// replacement into a reported copy failure.
/// </summary>
internal static class PostCommitCleanup
{
    internal static bool TryDeleteRegularFile(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        try
        {
            if (!File.Exists(path))
                return !Directory.Exists(path);
            WindowsPath.EnsureRegularFile(path, label);
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}