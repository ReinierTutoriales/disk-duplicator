using System.Reflection;

namespace RepartoCopier.WinUI;

internal static class BuildInfo
{
    // The .NET SDK appends "+<SourceRevisionId>" to AssemblyInformationalVersion. CI passes the
    // checked-out commit SHA and local builds read it from git (see RepartoCopier.WinUI.csproj).
    internal static string? Revision(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var plus = informationalVersion.IndexOf('+');
        if (plus < 0) return null;
        var revision = informationalVersion[(plus + 1)..].Trim();
        return revision.Length == 40 && revision.All(char.IsAsciiHexDigit)
            ? revision.ToLowerInvariant() : null;
    }

    internal static string? InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
