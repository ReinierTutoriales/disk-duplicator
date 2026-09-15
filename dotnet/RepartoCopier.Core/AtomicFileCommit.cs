namespace RepartoCopier.Core;

/// <summary>
/// Commits a durable temporary file into its final destination using the smallest
/// available namespace operation. Existing files use the operating system's
/// replace primitive instead of a manual destination->backup->destination dance.
/// </summary>
internal static class AtomicFileCommit
{
    internal static void Commit(string part, string destination, string backup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(backup);

        if (!File.Exists(destination))
        {
            File.Move(part, destination);
            return;
        }

        WindowsPath.EnsureRegularFile(destination, "El archivo de destino");
        DeleteBackupIfPresent(backup);

        try
        {
            File.Replace(part, destination, backup, ignoreMetadataErrors: false);
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