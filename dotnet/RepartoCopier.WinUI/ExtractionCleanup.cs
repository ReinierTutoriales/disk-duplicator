namespace RepartoCopier.WinUI;

/// <summary>
/// The single-file executable unpacks itself once per version under %TEMP%\.net\&lt;app&gt;\&lt;bundle id&gt;. .NET never
/// removes the folders of older versions, so each update would leave ~200 MB behind. On start this removes them,
/// best effort and in the background. A folder is only deleted after it could be renamed: Windows refuses that
/// rename while any of its files is open, so a still-running older version is never touched.
/// </summary>
internal static class ExtractionCleanup
{
    internal static void StartInBackground() => _ = Task.Run(() =>
    {
        try { RemoveOlderExtractions(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    });

    internal static void RemoveOlderExtractions()
    {
        var current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        // Only a self-extracted bundle runs from a folder other than the executable's own.
        if (processDirectory is null || string.Equals(Path.TrimEndingDirectorySeparator(processDirectory), current,
                StringComparison.OrdinalIgnoreCase))
            return;

        var appFolder = Path.GetDirectoryName(current);
        var appName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (appFolder is null || appName is null ||
            !string.Equals(Path.GetFileName(appFolder), appName, StringComparison.OrdinalIgnoreCase))
            return; // not the layout .NET uses: leave everything alone

        foreach (var sibling in Directory.EnumerateDirectories(appFolder))
        {
            if (string.Equals(sibling, current, StringComparison.OrdinalIgnoreCase))
                continue;
            var attributes = File.GetAttributes(sibling);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            var doomed = sibling + ".old-" + Environment.ProcessId;
            try
            {
                Directory.Move(sibling, doomed); // fails while another process uses it
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            try { Directory.Delete(doomed, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
