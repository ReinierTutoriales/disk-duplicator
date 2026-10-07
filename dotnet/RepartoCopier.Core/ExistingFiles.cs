namespace RepartoCopier.Core;

/// <summary>
/// What the engine may do with a destination file that already exists when the copy is prepared.
/// A plan without a policy is an undecided plan: the engine refuses to write over existing files.
/// </summary>
public enum ExistingFilePolicy
{
    /// <summary>Existing files are never touched; only missing files are copied.</summary>
    KeepExisting,

    /// <summary>Existing files with the same size and identical bytes are skipped; different ones are replaced.</summary>
    ReplaceDifferent,

    /// <summary>Every file that already existed when the copy was prepared is replaced without comparing it.</summary>
    ReplaceAll,

    /// <summary>Same size and last-write time are skipped without reading content; other existing files are replaced.</summary>
    ReplaceMetadataDifferent,
}

public sealed record DestinationConflict(
    string Destination,
    int ExistingFiles,
    int TotalFiles,
    IReadOnlyList<string> Examples);

/// <summary>
/// Raised before any destination is modified when files already exist and the plan has no policy for them.
/// </summary>
public sealed class ExistingFilesConflictException : IOException
{
    public ExistingFilesConflictException(IReadOnlyList<DestinationConflict> destinations)
        : base("Hay archivos que ya existen en el destino. Elige conservarlos o reemplazarlos antes de copiar.")
    {
        Destinations = destinations;
    }

    public IReadOnlyList<DestinationConflict> Destinations { get; }
}
