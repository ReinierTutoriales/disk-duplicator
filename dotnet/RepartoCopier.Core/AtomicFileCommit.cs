namespace RepartoCopier.Core;

/// <summary>
/// Commits a durable temporary file into its final destination using the smallest
/// available namespace operation. Existing files use the operating system's
/// replace primitive instead of a manual destination->backup->destination dance,
/// and only when the caller was explicitly authorised to replace them.
/// </summary>
internal static class AtomicFileCommit
{
    internal static void Commit(string part, string destination, string backup, bool allowReplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(backup);

        if (!File.Exists(destination))
        {
            try
            {
                RetryWhileShared(() => File.Move(part, destination));
            }
            catch (IOException) when (File.Exists(destination))
            {
                // The file appeared after the destination was analysed. Never replace it silently.
                throw new IOException($"El destino apareció durante la copia y no se reemplazó: {destination}");
            }
            return;
        }

        if (!allowReplace)
            throw new IOException($"El destino ya existe y no se autorizó reemplazarlo: {destination}");

        WindowsPath.EnsureRegularFile(destination, "El archivo de destino");
        DeleteBackupIfPresent(backup);

        try
        {
            RetryWhileShared(() => File.Replace(part, destination, backup, ignoreMetadataErrors: false));
        }
        catch (Exception commitError)
        {
            // ReplaceFile normally keeps the replaced path valid on failure. If a
            // filesystem edge case leaves only the generated backup, restore it.
            if (!File.Exists(destination) && File.Exists(backup))
            {
                try
                {
                    File.Move(backup, destination);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"CRÍTICO: falló el reemplazo de {destination} y también restaurar {backup}.",
                        new AggregateException(commitError, restoreError));
                }
            }

            throw new IOException(
                File.Exists(backup)
                    ? $"No se pudo reemplazar {destination}; el backup permanece en {backup}."
                    : $"No se pudo reemplazar {destination}.",
                commitError);
        }

        PostCommitCleanup.TryDeleteRegularFile(backup, "El backup de reemplazo");
    }

    /// <summary>
    /// Antivirus and indexers often open a file for a moment right after it is closed. A sharing or lock
    /// violation (32/33) at that moment is retried for up to about 5 seconds instead of failing the file.
    /// </summary>
    private static void RetryWhileShared(Action commit)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                commit();
                return;
            }
            catch (IOException ex) when (attempt < 20 && (ex.HResult & 0xFFFF) is 32 or 33)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void DeleteBackupIfPresent(string backup)
    {
        if (Directory.Exists(backup))
            throw new IOException($"El backup de reemplazo no puede ser una carpeta: {backup}");
        if (!File.Exists(backup))
            return;
        WindowsPath.EnsureRegularFile(backup, "El backup de reemplazo");
        File.Delete(backup);
    }
}