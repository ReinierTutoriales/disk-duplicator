$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content $Path -Raw
    if (-not $text.Contains($Old)) { throw "Expected block not found in $Path" }
    $text = $text.Replace($Old, $New)
    Set-Content $Path $text -NoNewline
}

$topology = 'dotnet/RepartoCopier.Core/StorageTopology.cs'
$oldProbe = @'
        if (!TryGetDeviceNumber(handle, out var physicalDevice, out var partition, out var deviceNumberError))
        {
            return Unknown(
                full,
                volumeRoot,
                deviceNumberError,
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
'@
$newProbe = @'
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
'@
Replace-Exact $topology $oldProbe $newProbe

$marker = @'
    private static bool TryGetDeviceNumber(
'@
$insert = @'
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
'@
Replace-Exact $topology $marker $insert

$oldConst = @'
        internal const uint IoctlStorageGetDeviceNumber = 0x002D1080;
        internal const uint IoctlStorageQueryProperty = 0x002D1400;
'@
$newConst = @'
        internal const uint IoctlStorageGetDeviceNumber = 0x002D1080;
        internal const uint IoctlStorageQueryProperty = 0x002D1400;
        internal const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
'@
Replace-Exact $topology $oldConst $newConst

$tests = 'dotnet/RepartoCopier.Core.Tests/StorageTopologyTests.cs'
$testMarker = @'
    [TestMethod]
    public void InspectDestinationsReturnsOneEntryPerDestination()
'@
$testInsert = @'
    [TestMethod]
    public void SingleDiskExtentFallbackRecoversPhysicalIdentityWithoutAThrottlePolicy()
    {
        var descriptor = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0, 4), 1);
        var offset = IntPtr.Size == 8 ? 8 : 4;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(offset, 4), 27);

        Assert.IsTrue(StorageTopology.TryParseSingleDiskExtent(descriptor, IntPtr.Size, out var disk));
        Assert.AreEqual((uint)27, disk);
    }

    [TestMethod]
    public void MultiDiskExtentDoesNotPretendToHaveOnePhysicalIdentity()
    {
        var descriptor = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0, 4), 2);

        Assert.IsFalse(StorageTopology.TryParseSingleDiskExtent(descriptor, IntPtr.Size, out _));
    }

    [TestMethod]
    public void InspectDestinationsReturnsOneEntryPerDestination()
'@
Replace-Exact $tests $testMarker $testInsert

$schedulerTests = 'dotnet/RepartoCopier.Core.Tests/DeviceSchedulerTests.cs'
$schedulerMarker = @'
    [TestMethod]
    public void SourceAndDestinationOnSameDiskStartAtOneButAreNotCappedThere()
'@
$schedulerInsert = @'
    [TestMethod]
    public void PhysicalCollisionSharesOnlyTheCollidingDiskAndLeavesOtherHardwareIndependent()
    {
        var source = Device(@"C:\source", 1, "NVMe", StorageMediaKind.SolidState, trim: true);
        var first = Device(@"E:\copy", 4, "SATA", StorageMediaKind.SolidState, trim: true);
        var second = Device(@"F:\copy", 4, "USB", StorageMediaKind.SolidState, trim: true);
        var independent = Device(@"G:\copy", 9, "NVMe", StorageMediaKind.SolidState, trim: true);

        using var map = DeviceSchedulerMap.Create(source, [first, second, independent]);

        Assert.HasCount(2, map.Schedulers);
        Assert.AreSame(map.For(first), map.For(second));
        Assert.AreNotSame(map.For(first), map.For(independent));
        Assert.AreEqual(16, map.For(independent).InitialQueueDepth);
    }

    [TestMethod]
    public void SourceAndDestinationOnSameDiskStartAtOneButAreNotCappedThere()
'@
Replace-Exact $schedulerTests $schedulerMarker $schedulerInsert

Write-Host 'Physical identity migration applied.'
