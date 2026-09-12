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
    ulong AllocationGranularity);

internal static class PreflightSafety
{
    private const ulong GiB = 1024UL * 1024UL * 1024UL;
    private const ulong MinFreeReserve = 1UL * GiB;
    private const ulong MaxFreeReserve = 16UL * GiB;

    internal static string CanonicalExisting(string path, string label)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new IOException($"No se pudo resolver {label} {full}: la ruta no existe.");
        return WindowsNative.GetFinalPath(full, label);
    }

    internal static string[] ValidateAndCanonicalizeDestinations(
        string overlapPath,
        IEnumerable<string> effectiveDestinations)
    {
        var source = CanonicalExisting(overlapPath, "origen");
        var canonical = new List<string>();

        foreach (var requested in effectiveDestinations)
        {
            var full = Path.GetFullPath(requested);
            if (PathsOverlap(source, full))
                throw new IOException($"El destino {requested} se solapa con el origen.");
            Directory.CreateDirectory(full);
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

    internal static SourceTreeScan ScanDirectory(string sourceRoot)
    {
        var directories = new List<string>();
        var files = new List<ScannedFile>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex)
            {
                throw new IOException($"No se pudo enumerar {directory}: {ex.Message}", ex);
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex)
                {
                    throw new IOException($"No se pudo inspeccionar {entry}: {ex.Message}", ex);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(
                        $"No se permite copiar un symlink, junction o reparse point: {entry}");

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(Path.GetRelativePath(sourceRoot, entry));
                    pending.Push(entry);
                    continue;
                }

                if (!File.Exists(entry))
                    throw new IOException($"Entrada de origen no soportada: {entry}");

                var info = new FileInfo(entry);
                files.Add(new ScannedFile(
                    entry,
                    Path.GetRelativePath(sourceRoot, entry),
                    info.Length,
                    info.LastWriteTimeUtc));
            }
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new SourceTreeScan(files, directories);
    }

    internal static void ValidateSourceTreeSnapshot(string sourceRoot, SourceTreeScan expected)
    {
        WindowsPath.EnsureNormalDirectory(sourceRoot, "El origen");
        var current = ScanDirectory(sourceRoot);

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

    internal static void ValidateDestinationLayout(
        string destinationRoot,
        IEnumerable<string> directories,
        IEnumerable<ScannedFile> files)
    {
        foreach (var relative in directories)
        {
            var target = Path.Combine(destinationRoot, relative);
            if (File.Exists(target))
                throw new IOException(
                    $"Conflicto en {target}: el origen requiere una carpeta, pero el destino contiene un archivo.");
            if (Directory.Exists(target) && WindowsPath.IsReparsePoint(target))
                throw new IOException($"La carpeta de destino es un reparse point: {target}");
        }

        foreach (var file in files)
        {
            var current = destinationRoot;
            var parts = file.RelativePath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                if (!File.Exists(current) && !Directory.Exists(current))
                    continue;
                if (WindowsPath.IsReparsePoint(current))
                    throw new IOException($"La ruta de destino contiene un reparse point: {current}");
                if (index < parts.Length - 1 && !Directory.Exists(current))
                    throw new IOException($"Componente de destino ya no es carpeta: {current}");
                if (index == parts.Length - 1 && Directory.Exists(current))
                    throw new IOException(
                        $"Conflicto en {current}: el origen requiere un archivo, pero el destino contiene una carpeta.");
            }
        }
    }

    internal static void EnsureFreeSpace(
        string destinationRoot,
        IReadOnlyList<ScannedFile> files,
        IReadOnlySet<string>? skippedRelativePaths = null)
    {
        if (files.Count == 0)
            return;

        var volume = WindowsNative.GetVolumeMetrics(destinationRoot);
        var granularity = Math.Max(1UL, volume.AllocationGranularity);
        Int128 committedDelta = 0;
        Int128 peakExtra = 0;
        ulong bytesToWrite = 0;

        foreach (var file in files)
        {
            if (skippedRelativePaths?.Contains(file.RelativePath) == true)
                continue;
            var destination = Path.Combine(destinationRoot, file.RelativePath);
            ulong oldAllocation = 0;
            if (File.Exists(destination))
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
        var reserve = bytesToWrite == 0 ? 0UL : ReserveForVolume(volume.TotalBytes);
        var required = SaturatingAdd(peak, reserve);
        if (volume.AvailableBytes >= required)
            return;

        var missing = required - volume.AvailableBytes;
        throw new IOException(
            $"Espacio insuficiente en {destinationRoot}. Pico requerido: {peak} bytes + reserva: {reserve} bytes; disponible: {volume.AvailableBytes} bytes; faltan: {missing} bytes.");
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

    internal static ulong ReserveForVolume(ulong totalBytes)
    {
        var onePercent = totalBytes / 100UL;
        return Math.Clamp(onePercent, MinFreeReserve, MaxFreeReserve);
    }

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

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(
                $"No se permite usar un enlace simbólico, junction o reparse point como {label}: {path}.");
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

internal static class WindowsNative
{
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameDos = 0;

    internal static string GetFinalPath(string path, string label)
    {
        using var handle = CreateFileW(
            path,
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
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
            throw new IOException($"No se pudo determinar el volumen de {path}.");

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
        return new VolumeMetrics(available, total, granularity);
    }

#pragma warning disable SYSLIB1054 // StringBuilder marshaling is clearer here than hand-written buffers.
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
