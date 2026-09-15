using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class AtomicFileCommitTests
{
    [TestMethod]
    public void NewDestinationUsesPartAsFinalFile()
    {
        using var temp = new TempScope();
        var part = Path.Combine(temp.Path, "payload.part");
        var destination = Path.Combine(temp.Path, "payload.bin");
        var backup = Path.Combine(temp.Path, "payload.backup");
        File.WriteAllBytes(part, [1, 2, 3, 4]);

        AtomicFileCommit.Commit(part, destination, backup);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(destination));
        Assert.IsFalse(File.Exists(part));
        Assert.IsFalse(File.Exists(backup));
    }

    [TestMethod]
    public void ExistingDestinationUsesReplacementPrimitiveAndRemovesBackupAfterSuccess()
    {
        using var temp = new TempScope();
        var part = Path.Combine(temp.Path, "payload.part");
        var destination = Path.Combine(temp.Path, "payload.bin");
        var backup = Path.Combine(temp.Path, "payload.backup");
        File.WriteAllBytes(destination, [9, 9, 9]);
        File.WriteAllBytes(part, [1, 2, 3, 4, 5]);

        AtomicFileCommit.Commit(part, destination, backup);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(destination));
        Assert.IsFalse(File.Exists(part));
        Assert.IsFalse(File.Exists(backup));
    }

    [TestMethod]
    public void PostCommitCleanupDoesNotFailAValidCommitWhenBackupIsTemporarilyLocked()
    {
        using var temp = new TempScope();
        var backup = Path.Combine(temp.Path, "locked.backup");
        File.WriteAllBytes(backup, [7, 8, 9]);

        using (var held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.IsFalse(PostCommitCleanup.TryDeleteRegularFile(backup, "backup bloqueado"));
            Assert.IsTrue(File.Exists(backup));
        }

        Assert.IsTrue(PostCommitCleanup.TryDeleteRegularFile(backup, "backup liberado"));
        Assert.IsFalse(File.Exists(backup));
    }

    private sealed class TempScope : IDisposable
    {
        internal TempScope()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RepartoCopier-atomic-commit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
