using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class PathSafetyTests
{
    [TestMethod]
    public void IsReparsePointFailsClosedWhenAttributesCannotBeRead()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"repartocopier-missing-{Guid.NewGuid():N}", "missing.bin");

        var error = Assert.ThrowsExactly<IOException>(() => WindowsPath.IsReparsePoint(missing));

        StringAssert.Contains(error.Message, "No se pudo validar de forma segura");
        Assert.IsNotNull(error.InnerException);
    }

    [TestMethod]
    public void CorePathSafetyAcceptsExistingPathLongerThanMaxPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-longpath-{Guid.NewGuid():N}");
        try
        {
            var current = root;
            while (current.Length < 300)
                current = Path.Combine(current, "segment-0123456789abcdef");

            Directory.CreateDirectory(current);
            var file = Path.Combine(current, "payload.bin");
            File.WriteAllBytes(file, [1, 2, 3, 4]);

            Assert.IsTrue(file.Length > 260, $"La ruta de prueba debe superar MAX_PATH: {file.Length}");
            WindowsPath.EnsureNormalDirectory(current, "directorio largo");
            WindowsPath.EnsureRegularFile(file, "archivo largo");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(file));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
