using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

internal sealed record ScannedFile(
    string FullPath,
    string RelativePath,
    long Size,
    DateTime LastWriteTimeUtc);

internal sealed record SourceTreeScan(
    IReadOnlyList<ScannedFile> Files,
    IReadOnlyList<string> Directories);

internal readonly record struct VolumeMetrics(
    ulong AvailableBytes,
    ulong TotalBytes,
    ulong AllocationGranularity,
    string VolumeId);

internal sealed record DestinationSpaceRequirement(
    string DestinationRoot,
    VolumeMetrics Volume,
    ulong PeakExtraBytes,
    ulong BytesToWrite);

internal static class PreflightSafety
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;
    private const ulong MinFreeReserve = 1UL * GiB;
    private const ulong MaxFreeReserve = 16UL * GiB;
    private const ulong MinDataFreeReserve = 64UL * 1024UL * 1024UL;
    private const ulong MaxDataFreeReserve = 1UL * GiB;
    private static readonly Lazy<string?> SystemVolumeId = new(() =>
    {
        try { return WindowsNative.GetVolumeMetrics(Environment.SystemDirectory).VolumeId; }
        catch (Exception) { return null; } // unknown: every volume keeps the conservative system reserve
    });

    internal static void ValidateRecoveryPaths(string source, IReadOnlyList<string> destinations)
    {
        foreach (var root in destinations)
        {
            var state = StateLayout.StateDirectoryFor(root);
            var locks = Path.Combine(Path.GetDirectoryName(state)!, ".locks");
            foreach (var ownedPath in new[] { state, locks })
            {
                if (PathsOverlap(source, ownedPath))
                    throw new IOException($"El origen se solapa con el estado de recuperación: {ownedPath}");
                foreach (var destination in destinations)
                    // A single-file copy may target a drive/share root, whose
                    // state necessarily lives beneath that root. Directory copies
                    // already use a named child as their effective destination.
                    if (PathsOverlap(destination, ownedPath) &&
                        !WindowsPath.SamePath(destination, Path.GetPathRoot(destination)!))
                        throw new IOException($"El destino se solapa con el estado de recuperación: {ownedPath}");
            }
        }
    }

    internal static string CanonicalExisting(string path, string label)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new IOException($"No se pudo resolver {label} {full}: la ruta no existe.");
        return WindowsNative.GetFinalPath(full, label);
    }

    internal static string[] ValidateAndCanonicalizeDestinations(
        string overlapPath,
        IEnumerable<string> effectiveDestinations,
        CancellationToken token = default,
        List<string>? created = null)
    {
        var source = CanonicalExisting(overlapPath, "origen");
        var canonical = new List<string>();

        foreach (var requested in effectiveDestinations)
        {
            token.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(requested);
            if (PathsOverlap(source, full))
                throw new IOException($"El destino {requested} se solapa con el origen.");
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
                created?.Add(full);
            }
            RejectReparse(full, "destino");
            var destination = CanonicalExisting(full, "destino");
            WindowsPath.EnsureNormalDirectory(destination, "El destino");

            if (PathsOverlap(source, destination))
                throw new IOException($"El destino {requested} se solapa con el origen.");

            WritableProbe(destination);
            canonical.Add(destination);
        }

        for (var i = 0; i < canonical.Count; i++)
        {
            for (var j = i + 1; j < canonical.Count; j++)
            {
                if (PathsOverlap(canonical[i], canonical[j]))
                    throw new IOException(
                        $"Los destinos {canonical[i]} y {canonical[j]} se solapan entre sí.");
            }
        }

        return [.. canonical];
    }

    /// <summary>Best effort: removes destination folders this preparation created, only while still empty.</summary>
    internal static void RemoveCreatedEmptyDirectories(IReadOnlyList<string> created)
    {
        for (var index = created.Count - 1; index >= 0; index--)
        {
            try
            {
                if (Directory.Exists(created[index]) && !Directory.EnumerateFileSystemEntries(created[index]).Any())
                    Directory.Delete(created[index]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cleanup must never hide the preparation error that triggered it.
            }
        }
    }

    /// <summary>
    /// FAT and FAT32 cannot hold a file of 4 GiB or more. Fail before writing anything instead of
    /// after copying gigabytes into a destination that can never accept the file.
    /// </summary>
    internal static void RejectFilesTooLargeForFileSystem(
        IReadOnlyList<string> destinationRoots,
        IReadOnlyList<StorageDeviceInfo> destinationDevices,
        IReadOnlyList<ScannedFile> files)
    {
        const long fatMaximumFileBytes = uint.MaxValue;
        ScannedFile? largest = null;
        foreach (var file in files)
        {
            if (file.Size > fatMaximumFileBytes && (largest is null || file.Size > largest.Size))
                largest = file;
        }
        if (largest is null)
            return;
        for (var slot = 0; slot < destinationRoots.Count; slot++)
        {
            if (destinationDevices[slot].FileSystem.ToUpperInvariant() is "FAT" or "FAT12" or "FAT16" or "FAT32")
                throw new IOException(
                    $"{destinationRoots[slot]} usa {destinationDevices[slot].FileSystem}, que no admite archivos de 4 GiB o más " +
                    $"({largest.RelativePath}). Formatea esa unidad como exFAT o NTFS.");
        }
    }

    internal static SourceTreeScan ScanDirectory(string sourceRoot, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var directories = new List<string>();
        var files = new List<ScannedFile>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            // Type and attributes come with the directory listing itself; a file then costs one metadata query
            // (instead of three) for its exact size and time. Directory entries of hard-linked files can lag
            // behind the file, so size and time are not taken from the listing. Only entries carrying the
            // reparse attribute cost one more lookup (their tag).
            foreach (var entry in EnumerateDirectoryEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (WindowsPath.IsLink(entry.FullName, entry.Attributes))
                    throw new IOException(
                        $"No se permite copiar un symlink, junction o acceso directo de aplicación: {entry.FullName}");

                if (entry is DirectoryInfo)
                {
                    directories.Add(Path.GetRelativePath(sourceRoot, entry.FullName));
                    pending.Push(entry.FullName);
                    continue;
                }

                var file = (FileInfo)entry;
                try
                {
                    file.Refresh();
                }
                catch (Exception ex)
                {
                    throw new IOException($"No se pudo inspeccionar {file.FullName}: {ex.Message}", ex);
                }
                if (!file.Exists)
                    throw new IOException($"Entrada de origen no soportada: {file.FullName}");
                files.Add(new ScannedFile(
                    file.FullName,
                    Path.GetRelativePath(sourceRoot, file.FullName),
                    file.Length,
                    file.LastWriteTimeUtc));
            }
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new SourceTreeScan(files, directories);
    }

    private static IEnumerable<FileSystemInfo> EnumerateDirectoryEntries(string directory)
    {
        IEnumerator<FileSystemInfo> enumerator;
        try
        {
            enumerator = new DirectoryInfo(directory).EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (Exception ex)
        {
            throw new IOException($"No se pudo enumerar {directory}: {ex.Message}", ex);
        }

        using (enumerator)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch (Exception ex)
                {
                    throw new IOException($"No se pudo enumerar {directory}: {ex.Message}", ex);
                }
                if (!moved)
                    yield break;
                yield return enumerator.Current;
            }
        }
    }

    internal static void ValidateSourceTreeSnapshot(string sourceRoot, SourceTreeScan expected, CancellationToken token = default)
    {
        WindowsPath.EnsureNormalDirectory(sourceRoot, "El origen");
        var current = ScanDirectory(sourceRoot, token);

        if (current.Directories.Count != expected.Directories.Count ||
            current.Files.Count != expected.Files.Count)
        {
            throw new IOException("La estructura del origen cambió durante la copia.");
        }

        for (var index = 0; index < expected.Directories.Count; index++)
        {
            if (!string.Equals(
                    expected.Directories[index],
                    current.Directories[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("La estructura de carpetas del origen cambió durante la copia.");
            }
        }

        for (var index = 0; index < expected.Files.Count; index++)
        {
            var before = expected.Files[index];
            var after = current.Files[index];
            if (!string.Equals(before.RelativePath, after.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                before.Size != after.Size ||
                before.LastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                throw new IOException($"El árbol de archivos del origen cambió durante la copia: {before.RelativePath}.");
            }
        }
    }

    internal enum ExistingFileAction
    {
        /// <summary>Nothing exists at the destination: the file is copied normally.</summary>
        Copy,

        /// <summary>The file exists and must stay untouched.</summary>
        Keep,

        /// <summary>The file exists and the chosen policy authorises replacing it.</summary>
        ReplaceAllowed,
    }

    internal static void RequireKnownPolicy(ExistingFilePolicy? policy)
    {
        if (policy is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(
                nameof(policy), value, "La política de archivos existentes no es válida.");
    }

    /// <summary>
    /// The only place that turns a policy into an action. An unknown or missing policy never
    /// authorises replacing a file.
    /// </summary>
    internal static ExistingFileAction Decide(ExistingFilePolicy? policy, bool exists)
    {
        RequireKnownPolicy(policy);
        if (!exists)
            return ExistingFileAction.Copy;
        return policy switch
        {
            ExistingFilePolicy.KeepExisting => ExistingFileAction.Keep,
            ExistingFilePolicy.ReplaceDifferent or ExistingFilePolicy.ReplaceAll or
                ExistingFilePolicy.ReplaceMetadataDifferent => ExistingFileAction.ReplaceAllowed,
            _ => throw new InvalidOperationException("Hay archivos existentes y ninguna política autoriza tocarlos."),
        };
    }

    /// <summary>Metadata-only heuristic: this never opens payload and does not prove content equality.
    /// Times compare exactly, except within the storage precision of the destination file system (see
    /// <see cref="TimestampTolerance"/>): a FAT copy can never hold the source's exact time.</summary>
    internal static bool MatchesMetadata(string destination, long sourceSize, DateTime sourceLastWriteTimeUtc,
        TimeSpan tolerance = default)
    {
        // One metadata query: existence, type, size and time arrive together.
        var info = new FileInfo(destination);
        if (!info.Exists)
        {
            if (Directory.Exists(destination))
                throw new IOException($"El archivo de destino no es un archivo regular seguro: {destination}");
            return false;
        }
        if (WindowsPath.IsLink(destination, info.Attributes))
            throw new IOException($"El archivo de destino es un enlace/junction/reparse point: {destination}");
        return MatchesMetadata(info, sourceSize, sourceLastWriteTimeUtc, tolerance);
    }

    /// <summary><see cref="MatchesMetadata(string, long, DateTime, TimeSpan)"/> for a file already listed and
    /// checked by a <see cref="DestinationIndex"/>.</summary>
    internal static bool MatchesMetadata(FileInfo existing, long sourceSize, DateTime sourceLastWriteTimeUtc,
        TimeSpan tolerance) =>
        existing.Length == sourceSize && (existing.LastWriteTimeUtc - sourceLastWriteTimeUtc).Duration() <= tolerance;

    /// <summary>Write-time precision of a file system: 2 s on FAT, 10 ms on exFAT, exact elsewhere
    /// (NTFS and ReFS keep 100 ns, the same unit as the source). This is what robocopy /FFT allows.</summary>
    internal static TimeSpan TimestampTolerance(string? fileSystem) => fileSystem?.ToUpperInvariant() switch
    {
        "FAT" or "FAT12" or "FAT16" or "FAT32" => TimeSpan.FromSeconds(2),
        "EXFAT" => TimeSpan.FromMilliseconds(10),
        _ => TimeSpan.Zero,
    };

    /// <summary>
    /// Read-only existence check of every source file in every destination. It never opens or hashes content.
    /// </summary>
    internal static bool[][] FindExistingFiles(
        IReadOnlyList<string> destinationRoots,
        IReadOnlyList<string> relativePaths,
        CancellationToken token = default)
    {
        var existing = new bool[relativePaths.Count][];
        for (var fileIndex = 0; fileIndex < relativePaths.Count; fileIndex++)
        {
            token.ThrowIfCancellationRequested();
            existing[fileIndex] = new bool[destinationRoots.Count];
            for (var slot = 0; slot < destinationRoots.Count; slot++)
                existing[fileIndex][slot] = File.Exists(Path.Combine(destinationRoots[slot], relativePaths[fileIndex]));
        }

        return existing;
    }

    internal static void ThrowIfExistingFiles(
        IReadOnlyList<string> destinationRoots,
        IReadOnlyList<string> relativePaths,
        bool[][] existing)
    {
        const int maxExamples = 3;
        var conflicts = new List<DestinationConflict>();
        for (var slot = 0; slot < destinationRoots.Count; slot++)
        {
            var count = 0;
            var examples = new List<string>(maxExamples);
            for (var fileIndex = 0; fileIndex < relativePaths.Count; fileIndex++)
            {
                if (!existing[fileIndex][slot])
                    continue;
                count++;
                if (examples.Count < maxExamples)
                    examples.Add(relativePaths[fileIndex]);
            }

            if (count > 0)
                conflicts.Add(new DestinationConflict(destinationRoots[slot], count, relativePaths.Count, examples));
        }

        if (conflicts.Count > 0)
            throw new ExistingFilesConflictException(conflicts);
    }

    internal static void ValidateDestinationLayout(
        string destinationRoot,
        IReadOnlyList<string> directories,
        IEnumerable<ScannedFile> files,
        CancellationToken token = default) =>
        DestinationIndex.Build(destinationRoot, directories, token).ValidateFiles(files, token);

    internal static void EnsureFreeSpace(
        string destinationRoot,
        IReadOnlyList<ScannedFile> files,
        IReadOnlySet<string>? skippedRelativePaths = null,
        CancellationToken token = default)
    {
        if (files.Count > 0)
            EnsureFreeSpaceForVolumes([EstimateDestinationSpace(destinationRoot, files, skippedRelativePaths, token)]);
    }

    internal static DestinationSpaceRequirement EstimateDestinationSpace(
        string destinationRoot,
        IReadOnlyList<ScannedFile> files,
        IReadOnlySet<string>? skippedRelativePaths = null,
        CancellationToken token = default,
        DestinationIndex? index = null)
    {

        var volume = WindowsNative.GetVolumeMetrics(destinationRoot);
        var granularity = Math.Max(1UL, volume.AllocationGranularity);
        Int128 committedDelta = 0;
        Int128 peakExtra = 0;
        ulong bytesToWrite = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (skippedRelativePaths?.Contains(file.RelativePath) == true)
                continue;
            var destination = Path.Combine(destinationRoot, file.RelativePath);
            ulong oldAllocation = 0;
            if (index is not null)
            {
                // Already listed and validated: no per-file probing.
                if (index.ExistingFile(file.RelativePath) is { } existing)
                    oldAllocation = RoundUp((ulong)existing.Length, granularity);
            }
            else if (File.Exists(destination))
            {
                WindowsPath.EnsureRegularFile(destination, "El archivo de destino");
                oldAllocation = RoundUp((ulong)new FileInfo(destination).Length, granularity);
            }
            else if (Directory.Exists(destination))
            {
                throw new IOException(
                    $"Conflicto en {destination}: el origen requiere un archivo normal.");
            }

            var newAllocation = RoundUp((ulong)file.Size, granularity);
            bytesToWrite = SaturatingAdd(bytesToWrite, (ulong)file.Size);
            var duringTemporary = committedDelta + (Int128)newAllocation;
            if (duringTemporary > peakExtra)
                peakExtra = duringTemporary;
            committedDelta += (Int128)newAllocation - (Int128)oldAllocation;
        }

        var peak = peakExtra <= 0
            ? 0UL
            : peakExtra >= (Int128)ulong.MaxValue
                ? ulong.MaxValue
                : (ulong)peakExtra;
        return new DestinationSpaceRequirement(destinationRoot, volume, peak, bytesToWrite);
    }

    internal static void EnsureFreeSpaceForVolumes(IEnumerable<DestinationSpaceRequirement> requirements)
    {
        foreach (var group in requirements.GroupBy(item => item.Volume.VolumeId, StringComparer.OrdinalIgnoreCase))
        {
            // Writers progress independently. Sum their individual peaks as a
            // conservative simultaneous upper bound, with one reserve per volume.
            var peak = group.Aggregate(0UL, (sum, item) => SaturatingAdd(sum, item.PeakExtraBytes));
            var available = group.Min(item => item.Volume.AvailableBytes);
            var systemVolume = SystemVolumeId.Value is not { } systemId ||
                               string.Equals(group.Key, systemId, StringComparison.OrdinalIgnoreCase);
            var reserve = group.Any(item => item.BytesToWrite > 0)
                ? ReserveForVolume(group.Max(item => item.Volume.TotalBytes), systemVolume) : 0UL;
            var required = SaturatingAdd(peak, reserve);
            if (available >= required) continue;
            throw new IOException(
                $"Espacio insuficiente en {string.Join(", ", group.Select(item => item.DestinationRoot))}. " +
                $"Pico conjunto requerido: {peak} bytes + reserva: {reserve} bytes; disponible: {available} bytes; faltan: {required - available} bytes.");
        }
    }

    internal static ulong RoundUp(ulong value, ulong granularity)
    {
        if (value == 0)
            return 0;
        granularity = Math.Max(1UL, granularity);
        var remainder = value % granularity;
        if (remainder == 0)
            return value;
        return SaturatingAdd(value, granularity - remainder);
    }

    /// <summary>
    /// Free space left untouched after the copy. The Windows volume keeps 1 % (1–16 GiB) so the system can
    /// still page, update and log. A data volume (USB stick, external or second disk) only keeps 0.1 %
    /// (64 MiB–1 GiB) for file-system metadata, so a stick can actually be filled.
    /// </summary>
    internal static ulong ReserveForVolume(ulong totalBytes, bool systemVolume = true) => systemVolume
        ? Math.Clamp(totalBytes / 100UL, MinFreeReserve, MaxFreeReserve)
        : Math.Clamp(totalBytes / 1000UL, MinDataFreeReserve, MaxDataFreeReserve);

    internal static bool PathsOverlap(string left, string right)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        return IsAncestor(a, b) || IsAncestor(b, a);
    }

    private static bool IsAncestor(string parent, string child)
    {
        var prefix = parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparse(string path, string label)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex)
        {
            throw new IOException($"No se pudo inspeccionar {label} {path}: {ex.Message}", ex);
        }

        if (WindowsPath.IsLink(path, attributes))
            throw new IOException(
                $"No se permite usar un enlace simbólico o junction como {label}: {path}.");
    }

    private static void WritableProbe(string directory)
    {
        var probe = Path.Combine(
            directory,
            $".repartocopier-write-test-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            stream.WriteByte(0x52);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            throw new IOException($"El destino no es escribible: {directory}: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                    File.Delete(probe);
            }
            catch
            {
                // The write test already established whether the destination is writable.
                // A cleanup error must not hide the original preflight result.
            }
        }
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}

/// <summary>
/// What a destination already holds under the folders a copy touches, read with one directory listing per
/// folder instead of several existence and attribute queries per file. A listed folder that is a file or a
/// link is reported before anything below it is read, so a junction is never followed.
/// </summary>
internal sealed class DestinationIndex
{
    private readonly string _root;
    private readonly Dictionary<string, FileSystemInfo> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refreshed = new(StringComparer.OrdinalIgnoreCase);

    private DestinationIndex(string root) => _root = root;

    internal static DestinationIndex Build(string root, IReadOnlyList<string> directories, CancellationToken token)
    {
        var index = new DestinationIndex(root);
        if (!Directory.Exists(root))
            return index;
        index.List(string.Empty);
        // Sorted so a folder is always validated before its children are listed.
        foreach (var relative in directories.Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(root, relative);
            if (!index._entries.TryGetValue(relative, out var entry))
                continue; // missing: nothing below it exists either
            if (entry is not DirectoryInfo)
                throw new IOException(
                    $"Conflicto en {target}: el origen requiere una carpeta, pero el destino contiene un archivo.");
            if (WindowsPath.IsLink(entry.FullName, entry.Attributes))
                throw new IOException($"La carpeta de destino es un reparse point: {target}");
            index.List(relative);
        }
        return index;
    }

    internal void ValidateFiles(IEnumerable<ScannedFile> files, CancellationToken token)
    {
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(file.RelativePath, out var entry))
                continue;
            var current = Path.Combine(_root, file.RelativePath);
            if (WindowsPath.IsLink(entry.FullName, entry.Attributes))
                throw new IOException($"La ruta de destino contiene un reparse point: {current}");
            if (entry is DirectoryInfo)
                throw new IOException(
                    $"Conflicto en {current}: el origen requiere un archivo, pero el destino contiene una carpeta.");
        }
    }

    /// <summary>The existing regular file at <paramref name="relative"/> with exact, current size and time,
    /// or null. Directory listings of hard-linked files can lag, so a hit is re-read once.</summary>
    internal FileInfo? ExistingFile(string relative)
    {
        if (!_entries.TryGetValue(relative, out var entry) || entry is not FileInfo file)
            return null;
        if (_refreshed.Add(relative))
        {
            file.Refresh();
            if (!file.Exists)
            {
                _entries.Remove(relative);
                return null;
            }
        }
        return file;
    }

    private void List(string relative)
    {
        var directory = relative.Length == 0 ? _root : Path.Combine(_root, relative);
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                _entries[relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name)] = entry;
        }
        catch (DirectoryNotFoundException)
        {
            // Removed between listings: treated as missing, like the per-path checks did.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"No se pudo enumerar {directory}: {ex.Message}", ex);
        }
    }
}

internal static class WindowsNative
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameDos = 0;

    internal static string GetFinalPath(string path, string label)
    {
        using var handle = CreateFileW(
            WindowsPath.Extended(Path.GetFullPath(path)),
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException(
                $"No se pudo resolver {label} {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
            if (length == 0)
                throw new IOException(
                    $"No se pudo resolver {label} {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            if (length < buffer.Capacity)
                return WindowsPath.NormalizeIdentity(buffer.ToString());
            capacity = checked((int)length + 1);
        }
    }

    internal static VolumeMetrics GetVolumeMetrics(string path)
    {
        var mountPath = new StringBuilder(32768);
        if (!GetVolumePathNameW(Path.GetFullPath(path), mountPath, (uint)mountPath.Capacity))
            throw new IOException($"No se pudo determinar el volumen de {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        var root = mountPath.ToString();
        var volumeName = new StringBuilder(64);
        // Remote shares do not have a local volume GUID. Keep their share root.
        var volumeId = GetVolumeNameForVolumeMountPointW(root, volumeName, (uint)volumeName.Capacity)
            ? volumeName.ToString() : root;

        if (!GetDiskFreeSpaceExW(root, out var available, out var total, out _))
            throw new IOException(
                $"No se pudo consultar espacio libre en {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        if (!GetDiskFreeSpaceW(
                root,
                out var sectorsPerCluster,
                out var bytesPerSector,
                out _,
                out _))
            throw new IOException(
                $"No se pudo consultar granularidad de asignación en {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var granularity = checked((ulong)sectorsPerCluster * bytesPerSector);
        return new VolumeMetrics(available, total, granularity, volumeId);
    }

#pragma warning disable SYSLIB1054 // StringBuilder marshaling is clearer here than hand-written buffers.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);
#pragma warning restore SYSLIB1054
}
