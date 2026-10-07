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
                File.Move(part, destination);
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