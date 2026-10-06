using System.Text.Json;
using RepartoCopier.Core;

namespace RepartoCopier.WinUI;

public sealed record CopyProfile(
    int Version,
    string Source,
    IReadOnlyList<string> Destinations,
    bool SkipExisting,
    bool ContinueOnError,
    bool ShutdownWhenFinished,
    bool VerifyAfterCopy = true)
{
    public const int CurrentVersion = 1;
}

internal static class CopyProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(CopyProfile profile) => JsonSerializer.Serialize(profile, Options);

    public static CopyProfile Deserialize(string json)
    {
        var profile = JsonSerializer.Deserialize<CopyProfile>(json, Options)
            ?? throw new InvalidDataException("El archivo de copia no contiene una configuración válida.");
        if (profile.Version != CopyProfile.CurrentVersion)
            throw new InvalidDataException($"Esta configuración usa una versión no compatible ({profile.Version}).");
        if (string.IsNullOrWhiteSpace(profile.Source))
            throw new InvalidDataException("La configuración no contiene un origen.");
        if (profile.Destinations is null || profile.Destinations.Count == 0)
            throw new InvalidDataException("La configuración no contiene destinos.");
        _ = CopyPlan.Create(profile.Source, profile.Destinations, profile.SkipExisting, profile.ContinueOnError);
        return profile;
    }
}
