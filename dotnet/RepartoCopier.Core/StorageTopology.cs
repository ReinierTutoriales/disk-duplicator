using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RepartoCopier.Core;

public enum StorageMediaKind
{
    Unknown,
    SolidState,
    Rotational,
}

public sealed record StorageDeviceInfo(
    string DestinationRoot,
    string VolumeRoot,
    uint? PhysicalDeviceNumber,
    uint? PartitionNumber,
    string BusType,
    StorageMediaKind MediaKind,
    bool? Removable,
    uint? LogicalSectorBytes,
    uint? PhysicalSectorBytes,
    bool ProbeSucceeded,
    string? ProbeError,
    bool SharesPhysicalDevice = false,
    string FileSystem = "Unknown",
    DriveType DriveType = DriveType.Unknown,
    bool IsNetwork = false,
    bool SupportsPreallocation = false,
    bool? TrimEnabled = null,
    uint? SectorAlignmentOffsetBytes = null,
    uint? VolumeFlags = null,
    uint? MaximumComponentLength = null,
    long? AvailableFreeSpaceBytes = null,
    long? TotalSpaceBytes = null)
{
    public string PhysicalDeviceId => PhysicalDeviceNumber is uint number
        ? $"PhysicalDisk{number}"
        : IsNetwork
            ? $"Network:{VolumeRoot}"
            : $"Volume:{VolumeRoot}";

    public bool HasKnownSectorAlignment =>
        LogicalSectorBytes is > 0 &&
        PhysicalSectorBytes is > 0 &&
        SectorAlignmentOffsetBytes.HasValue;
}

public sealed record SharedPhysicalDeviceGroup(
    uint PhysicalDeviceNumber,
    IReadOnlyList<string> DestinationRoots);

public sealed record StorageTopologySnapshot(
    IReadOnlyList<StorageDeviceInfo> Destinations,
    IReadOnlyList<SharedPhysicalDeviceGroup> SharedPhysicalDevices)
{
    public static StorageTopologySnapshot Empty { get; } = new([], []);
}

/// <summary>
/// Best-effort Windows storage topology inspection. A topology probe must never
/// make an otherwise valid copy fail; unsupported/network volumes are reported
/// conservatively and the copy engine keeps a safe fallback profile.
/// </summary>
public static class StorageTopology
{
    public static StorageTopologySnapshot InspectDestinations(IEnumerable<string> destinationRoots)
    {
        ArgumentNullException.ThrowIfNull(destinationRoots);
        return BuildSnapshot(destinationRoots.Select(InspectDestination));
    }

    internal static StorageTopologySnapshot BuildSnapshot(IEnumerable<StorageDeviceInfo> devices)
    {
        var materialized = devices.ToArray();
        var shared = materialized
            .Where(item => item.ProbeSucceeded && item.PhysicalDeviceNumber.HasValue)
            .GroupBy(item => item.PhysicalDeviceNumber!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => new SharedPhysicalDeviceGroup(
                group.Key,
                group.Select(item => item.DestinationRoot).ToArray()))
            .OrderBy(group => group.PhysicalDeviceNumber)
            .ToArray();

        if (shared.Length == 0)
            return new StorageTopologySnapshot(materialized, shared);

        var sharedNumbers = shared.Select(group => group.PhysicalDeviceNumber).ToHashSet();
        var annotated = materialized
            .Select(item => item with
            {
                SharesPhysicalDevice = item.ProbeSucceeded &&
                                       item.PhysicalDeviceNumber is uint number &&
                                       sharedNumbers.Contains(number),
            })
            .ToArray();
        return new StorageTopologySnapshot(annotated, shared);
    }

    internal static StorageDeviceInfo InspectDestination(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        var full = Path.GetFullPath(destinationRoot);
        var volumeRoot = Path.GetPathRoot(full) ?? string.Empty;

        var fileSystem = "Unknown";
        var driveType = DriveType.Unknown;
        long? availableFreeSpace = null;
        long? totalSpace = null;
        var volumeWarnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(volumeRoot))
        {
            try
            {
                var drive = new DriveInfo(volumeRoot);
                driveType = drive.DriveType;
                if (drive.IsReady)
                {
                    fileSystem = drive.DriveFormat;
                    availableFreeSpace = drive.AvailableFreeSpace;
                    totalSpace = drive.TotalSize;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                volumeWarnings.Add("No se pudo consultar el volumen administrado: " + ex.Message);
            }
        }

        var isNetwork = IsNetworkDestination(full, driveType);
        var supportsPreallocation = SupportsSafePreallocation(fileSystem, isNetwork);
        if (isNetwork)
        {
            return new StorageDeviceInfo(
                full,
                volumeRoot,
                null,
                null,
                "Network",
                StorageMediaKind.Unknown,
                null,
                null,
                null,
                true,
                volumeWarnings.Count == 0 ? null : string.Join(" ", volumeWarnings),
                false,
                fileSystem,
                driveType,
                true,
                false,
                null,
                null,
                null,
                null,
                availableFreeSpace,
                totalSpace);
        }

        if (volumeRoot.Length < 2 || volumeRoot[1] != ':')
        {
            var note = "El destino no es un volumen local con letra de unidad.";
            if (volumeWarnings.Count > 0)
                note += " " + string.Join(" ", volumeWarnings);
            return Unknown(
                full,
                volumeRoot,
                note,
                fileSystem,
                driveType,
                false,
                supportsPreallocation,
                availableFreeSpace,
                totalSpace);
        }

        var devicePath = $@"\\.\{char.ToUpperInvariant(volumeRoot[0])}:";
        using var handle = NativeMethods.CreateFileW(
            devicePath,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return Unknown(
                full,
                volumeRoot,
                $"No se pudo abrir el volumen para consultar topología: {error}",
                fileSystem,
                driveType,
                false,
                supportsPreallocation,
                availableFreeSpace,
                totalSpace);
        }

        uint? volumeFlags = null;
        uint? maximumComponentLength = null;
        if (TryGetVolumeInformation(handle, out var nativeFileSystem, out var maxComponent, out var flags, out var volumeError))
        {
            if (!string.IsNullOrWhiteSpace(nativeFileSystem))
                fileSystem = nativeFileSystem;
            maximumComponentLength = maxComponent;
            volumeFlags = flags;
            supportsPreallocation = SupportsSafePreallocation(fileSystem, isNetwork: false);
        }
        else
        {
            volumeWarnings.Add(volumeError);
        }

        uint physicalDevice;
        uint? partition;
        if (TryGetDeviceNumber(handle, out var probedPhysicalDevice, out var probedPartition, out var deviceNumberError))
        {
            physicalDevice = probedPhysicalDevice;
            partition = probedPartition;
        }
        else if (TryGetSingleDiskExtent(handle, out var extentPhysicalDevice, out var extentError))
        {
            physicalDevice = extentPhysicalDevice;
            partition = null;
            volumeWarnings.Add("Identidad física recuperada mediante VOLUME_DISK_EXTENTS tras fallar STORAGE_DEVICE_NUMBER.");
        }
        else
        {
            return Unknown(
                full,
                volumeRoot,
                deviceNumberError + " " + extentError,
                fileSystem,
                driveType,
                false,
                supportsPreallocation,
                availableFreeSpace,
                totalSpace,
                volumeFlags,
                maximumComponentLength);
        }

        var busType = "Unknown";
        bool? removable = null;
        var mediaKind = StorageMediaKind.Unknown;
        bool? trimEnabled = null;
        uint? logicalSector = null;
        uint? physicalSector = null;
        uint? sectorAlignmentOffset = null;
        var warnings = new List<string>(volumeWarnings);

        if (TryQueryProperty(handle, StoragePropertyId.Device, 64, out var deviceDescriptor, out var deviceError))
        {
            if (deviceDescriptor.Length >= 32)
            {
                removable = deviceDescriptor[10] != 0;
                busType = BusTypeName(BinaryPrimitives.ReadInt32LittleEndian(deviceDescriptor.AsSpan(28, 4)));
            }
            else
            {
                warnings.Add("Descriptor de dispositivo demasiado corto.");
            }
        }
        else
        {
            warnings.Add(deviceError);
        }

        if (TryQueryProperty(handle, StoragePropertyId.SeekPenalty, 16, out var seekDescriptor, out var seekError))
        {
            if (seekDescriptor.Length >= 9)
                mediaKind = seekDescriptor[8] == 0 ? StorageMediaKind.SolidState : StorageMediaKind.Rotational;
            else
                warnings.Add("Descriptor de seek penalty demasiado corto.");
        }
        else
        {
            warnings.Add(seekError);
        }

        if (TryQueryProperty(handle, StoragePropertyId.Trim, 16, out var trimDescriptor, out var trimError))
        {
            if (trimDescriptor.Length >= 9)
                trimEnabled = trimDescriptor[8] != 0;
            else
                warnings.Add("Descriptor de TRIM demasiado corto.");
        }
        else
        {
            warnings.Add(trimError);
        }

        if (TryQueryProperty(handle, StoragePropertyId.AccessAlignment, 32, out var alignmentDescriptor, out var alignmentError))
        {
            if (alignmentDescriptor.Length >= 28)
            {
                logicalSector = BinaryPrimitives.ReadUInt32LittleEndian(alignmentDescriptor.AsSpan(16, 4));
                physicalSector = BinaryPrimitives.ReadUInt32LittleEndian(alignmentDescriptor.AsSpan(20, 4));
                sectorAlignmentOffset = BinaryPrimitives.ReadUInt32LittleEndian(alignmentDescriptor.AsSpan(24, 4));
            }
            else
            {
                warnings.Add("Descriptor de alineación demasiado corto.");
            }
        }
        else
        {
            warnings.Add(alignmentError);
        }

        return new StorageDeviceInfo(
            full,
            volumeRoot,
            physicalDevice,
            partition,
            busType,
            mediaKind,
            removable,
            logicalSector,
            physicalSector,
            true,
            warnings.Count == 0 ? null : string.Join(" ", warnings),
            false,
            fileSystem,
            driveType,
            false,
            supportsPreallocation,
            trimEnabled,
            sectorAlignmentOffset,
            volumeFlags,
            maximumComponentLength,
            availableFreeSpace,
            totalSpace);
    }

    internal static bool SupportsSafePreallocation(string? fileSystem, bool isNetwork) =>
        !isNetwork &&
        (string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase));

    internal static bool IsNetworkDestination(string fullPath, DriveType driveType) =>
        driveType == DriveType.Network ||
        fullPath.StartsWith(@"\\", StringComparison.Ordinal) ||
        fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);

    private static StorageDeviceInfo Unknown(
        string destinationRoot,
        string volumeRoot,
        string error,
        string fileSystem = "Unknown",
        DriveType driveType = DriveType.Unknown,
        bool isNetwork = false,
        bool supportsPreallocation = false,
        long? availableFreeSpace = null,
        long? totalSpace = null,
        uint? volumeFlags = null,
        uint? maximumComponentLength = null) =>
        new(
            destinationRoot,
            volumeRoot,
            null,
            null,
            "Unknown",
            StorageMediaKind.Unknown,
            null,
            null,
            null,
            false,
            error,
            false,
            fileSystem,
            driveType,
            isNetwork,
            supportsPreallocation,
            null,
            null,
            volumeFlags,
            maximumComponentLength,
            availableFreeSpace,
            totalSpace);

    internal static bool TryParseSingleDiskExtent(ReadOnlySpan<byte> descriptor, int pointerSize, out uint physicalDevice)
    {
        physicalDevice = 0;
        if (pointerSize is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(pointerSize));
        if (descriptor.Length < 4)
            return false;

        var extentCount = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[..4]);
        if (extentCount != 1)
            return false;

        var firstExtentOffset = pointerSize == 8 ? 8 : 4;
        if (descriptor.Length < firstExtentOffset + sizeof(uint))
            return false;

        physicalDevice = BinaryPrimitives.ReadUInt32LittleEndian(
            descriptor.Slice(firstExtentOffset, sizeof(uint)));
        return true;
    }

    private static bool TryGetSingleDiskExtent(
        SafeFileHandle handle,
        out uint physicalDevice,
        out string error)
    {
        var output = new byte[4096];
        if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.IoctlVolumeGetVolumeDiskExtents,
                null,
                0,
                output,
                (uint)output.Length,
                out var returned,
                IntPtr.Zero))
        {
            physicalDevice = 0;
            error = "No se pudieron consultar los extents físicos del volumen: " +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var length = checked((int)Math.Min(returned, (uint)output.Length));
        var descriptor = output.AsSpan(0, length);
        if (!TryParseSingleDiskExtent(descriptor, IntPtr.Size, out physicalDevice))
        {
            var extentCount = descriptor.Length >= 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(descriptor[..4])
                : 0;
            error = extentCount > 1
                ? $"El volumen abarca {extentCount} discos físicos y no admite una identidad única."
                : "El descriptor de extents físicos no contiene una identidad única válida.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetDeviceNumber(
        SafeFileHandle handle,
        out uint physicalDevice,
        out uint partition,
        out string error)
    {
        var output = new byte[12];
        if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.IoctlStorageGetDeviceNumber,
                null,
                0,
                output,
                (uint)output.Length,
                out var returned,
                IntPtr.Zero))
        {
            physicalDevice = 0;
            partition = 0;
            error = "No se pudo obtener el número de disco físico: " +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        if (returned < 12)
        {
            physicalDevice = 0;
            partition = 0;
            error = $"Descriptor de número de dispositivo demasiado corto: {returned} bytes.";
            return false;
        }

        physicalDevice = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4, 4));
        partition = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8, 4));
        error = string.Empty;
        return true;
    }

    private static bool TryQueryProperty(
        SafeFileHandle handle,
        StoragePropertyId propertyId,
        int outputSize,
        out byte[] descriptor,
        out string error)
    {
        var query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(0, 4), (int)propertyId);
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(4, 4), 0); // PropertyStandardQuery
        var output = new byte[outputSize];
        if (!NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.IoctlStorageQueryProperty,
                query,
                (uint)query.Length,
                output,
                (uint)output.Length,
                out var returned,
                IntPtr.Zero) || returned == 0)
        {
            descriptor = [];
            error = $"No se pudo consultar {propertyId}: " +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        descriptor = output.AsSpan(0, checked((int)Math.Min(returned, (uint)output.Length))).ToArray();
        error = string.Empty;
        return true;
    }

    private static bool TryGetVolumeInformation(
        SafeFileHandle handle,
        out string fileSystem,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        out string error)
    {
        var volumeName = new StringBuilder(261);
        var fileSystemName = new StringBuilder(261);
        if (!NativeMethods.GetVolumeInformationByHandleW(
                handle,
                volumeName,
                (uint)volumeName.Capacity,
                out _,
                out maximumComponentLength,
                out fileSystemFlags,
                fileSystemName,
                (uint)fileSystemName.Capacity))
        {
            fileSystem = "Unknown";
            maximumComponentLength = 0;
            fileSystemFlags = 0;
            error = "No se pudo consultar información nativa del volumen: " +
                    new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        fileSystem = fileSystemName.ToString();
        error = string.Empty;
        return true;
    }

    private static string BusTypeName(int value) => value switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE1394",
        5 => "SSA",
        6 => "FibreChannel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "FileBackedVirtual",
        16 => "StorageSpaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        20 => "NVMe-oF",
        _ => "Unknown",
    };

    private enum StoragePropertyId
    {
        Device = 0,
        AccessAlignment = 6,
        SeekPenalty = 7,
        Trim = 8,
    }

    private static class NativeMethods
    {
        internal const uint OpenExisting = 3;
        internal const uint IoctlStorageGetDeviceNumber = 0x002D1080;
        internal const uint IoctlStorageQueryProperty = 0x002D1400;
        internal const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

#pragma warning disable SYSLIB1054 // SafeHandle + small marshalled buffers keep this interop auditable.
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[]? inBuffer,
            uint inBufferSize,
            byte[] outBuffer,
            uint outBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationByHandleW(
            SafeFileHandle file,
            StringBuilder volumeNameBuffer,
            uint volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            uint fileSystemNameSize);
#pragma warning restore SYSLIB1054
    }
}
