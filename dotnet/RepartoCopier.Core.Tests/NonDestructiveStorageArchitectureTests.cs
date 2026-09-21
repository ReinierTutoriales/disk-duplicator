using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class NonDestructiveStorageArchitectureTests
{
    [TestMethod]
    public void ProductCodeContainsNoVolumeFormattingPartitioningOrRawDiskWriteApis()
    {
        var root = FindRepositoryRoot();
        var productRoot = Path.Combine(root, "dotnet");
        var productFiles = Directory.EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}RepartoCopier.Core.Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var forbidden = new[]
        {
            "IOCTL_DISK_SET_DRIVE_LAYOUT",
            "IOCTL_DISK_CREATE_DISK",
            "IOCTL_DISK_DELETE_DRIVE_LAYOUT",
            "FSCTL_EXTEND_VOLUME",
            "FSCTL_LOCK_VOLUME",
            "FSCTL_DISMOUNT_VOLUME",
            "Format-Volume",
            "Clear-Disk",
            "Initialize-Disk",
            "New-Partition",
            "diskpart.exe",
            "\\\\.\\PhysicalDrive",
        };

        foreach (var path in productFiles)
        {
            var code = File.ReadAllText(path);
            foreach (var token in forbidden)
                Assert.IsFalse(
                    code.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"Código de producto no debe contener operación destructiva de volumen '{token}': {path}");
        }
    }

    [TestMethod]
    public void CopyEngineWritesThroughFilesystemPathsNotRawDeviceHandles()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var direct = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "DirectIoDestinationWriter.cs"));

        Assert.IsTrue(engine.Contains("Path.Combine(worker.Root, entry.RelativePath)", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("AtomicFileCommit.Commit", StringComparison.Ordinal));
        Assert.IsTrue(direct.Contains("CreateFileW(", StringComparison.Ordinal));
        Assert.IsFalse(direct.Contains("PhysicalDrive", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(direct.Contains("SetFilePointerEx", StringComparison.Ordinal));
        Assert.IsFalse(direct.Contains("WriteFile(", StringComparison.Ordinal));
        Assert.IsFalse(direct.Contains("FSCTL_", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(direct.Contains("IOCTL_DISK_", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
