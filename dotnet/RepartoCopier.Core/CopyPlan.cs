using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

public sealed record CopyPlan(
    string Source,
    IReadOnlyList<string> Destinations,
    ExistingFilePolicy? ExistingFiles,
    bool KeepGoing)
{
    public const int MaxDestinations = 256;

    public static CopyPlan Create(
        string source,
        IEnumerable<string> destinations,
        ExistingFilePolicy? existingFiles,
        bool keepGoing)
    {
        PreflightSafety.RequireKnownPolicy(existingFiles);
        source = (source ?? string.Empty).Trim();
        if (source.Length == 0)
            throw new ArgumentException("Selecciona un archivo o carpeta de origen.", nameof(source));

        var clean = new List<string>();
        foreach (var raw in destinations ?? [])
        {
            if (clean.Count >= MaxDestinations)
                throw new ArgumentException($"La copia supera el máximo de {MaxDestinations} destinos.", nameof(destinations));

            var destination = (raw ?? string.Empty).Trim();
            if (destination.Length == 0)
                throw new ArgumentException("La copia contiene un destino vacío.", nameof(destinations));
            if (WindowsPath.SamePath(source, destination))
                throw new ArgumentException("El origen no puede ser también un destino.", nameof(destinations));
            if (clean.Any(current => WindowsPath.SamePath(current, destination)))
                throw new ArgumentException("La copia contiene destinos duplicados.", nameof(destinations));

            clean.Add(destination);
        }

        if (clean.Count == 0)
            throw new ArgumentException("Agrega al menos un destino.", nameof(destinations));

        return new CopyPlan(source, clean, existingFiles, keepGoing);
    }

    public static int AppendUniqueDestinations(
        string source,
        IList<string> existing,
        IEnumerable<string> selected)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(selected);
        var added = 0;
        foreach (var raw in selected)
        {
            if (existing.Count >= MaxDestinations)
                break;

            var path = (raw ?? string.Empty).Trim();
            if (path.Length == 0 || WindowsPath.SamePath(path, source))
                continue;
            if (existing.Any(current => WindowsPath.SamePath(current, path)))
                continue;

            existing.Add(path);
            added++;
        }
        return added;
    }
}

public static class WindowsPath
{
    public static string NormalizeIdentity(string path)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        value = value.Replace('/', '\\');
        var trimmed = value.TrimEnd('\\');
        if (trimmed.Length == 0)
            return value;
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed + "\\";
        return trimmed;
    }

    public static bool SamePath(string left, string right) =>
        string.Equals(
            NormalizeIdentity(left),
            NormalizeIdentity(right),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a link: a symlink, junction, mount point or app execution alias. Other reparse points
    /// (OneDrive placeholders, WOF/CompactOS compression, deduplication) are ordinary files and folders
    /// and are copied normally.
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            return IsLink(path, File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"No se pudo validar de forma segura la ruta: {path}", ex);
        }
    }

    /// <summary>Same as <see cref="IsLink(string)"/> when the attributes are already known; the reparse tag
    /// is only read for entries that carry the reparse attribute.</summary>
    internal static bool IsLink(string path, FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0 && IsLinkTag(ReparseTag(path));

    // Name surrogates (bit 29) cover symlinks, junctions/mount points and WSL links; app execution aliases
    // are zero-byte stubs that cannot be read as data.
    internal static bool IsLinkTag(uint tag) => (tag & 0x20000000) != 0 || tag == IoReparseTagAppExecLink;

    private const uint IoReparseTagAppExecLink = 0x8000001B;

    private static uint ReparseTag(string path)
    {
        using var find = NativeMethods.FindFirstFileExW(
            Extended(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))),
            1, // FindExInfoBasic
            out var data,
            0, // FindExSearchNameMatch
            IntPtr.Zero,
            0);
        if (find.IsInvalid)
            throw new IOException(
                $"No se pudo leer el tipo de reparse point de {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return data.Reserved0;
    }

    /// <summary>
    /// Extended-length form for raw Win32 calls, so paths over 260 characters work even when the system
    /// long-path policy is off. Managed .NET file APIs already do this on their own.
    /// </summary>
    internal static string Extended(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }

    public static void EnsureNormalDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
            throw new IOException($"{label} no existe: {path}");
        if (IsLink(path))
            throw new IOException($"{label} es un enlace/junction/reparse point: {path}");
    }

    public static void EnsureRegularFile(string path, string label)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            throw new IOException($"{label} no es un archivo regular seguro: {path}");
        if (IsLink(path))
            throw new IOException($"{label} es un enlace/junction/reparse point: {path}");
    }

    private sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public FindHandle() : base(true) { }
        protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // A by-value string struct keeps this interop readable without unsafe code.
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern FindHandle FindFirstFileExW(
            string fileName, int infoLevel, out FindData data, int searchOp, IntPtr searchFilter, int flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr handle);
#pragma warning restore SYSLIB1054
    }
}
