using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ExistingContentComparisonTests
{
    [TestMethod]
    public async Task ExactComparisonHandlesShortTailEmptyAndDifferentFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"comparison-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var payload = new byte[5 * 1024 * 1024 + 137];
            new Random(19).NextBytes(payload);
            var source = Path.Combine(root, "source");
            var same = Path.Combine(root, "same");
            var different = Path.Combine(root, "different");
            var shorter = Path.Combine(root, "shorter");
            await File.WriteAllBytesAsync(source, payload);
            await File.WriteAllBytesAsync(same, payload);
            var changed = (byte[])payload.Clone();
            changed[0] ^= 1;
            await File.WriteAllBytesAsync(different, changed);
            await File.WriteAllBytesAsync(shorter, [1]);
            var reads = new long[3];
            var result = await ExistingContentComparer.CompareAsync(
                source, [same, different, shorter], payload.Length, CancellationToken.None,
                _ => ValueTask.CompletedTask, (slot, bytes) => Interlocked.Add(ref reads[slot], bytes));
            CollectionAssert.AreEqual(new[] { true, false, false }, result);
            Assert.AreEqual((long)payload.Length, reads[0]);
            Assert.IsTrue(reads[1] < payload.Length, "A first-block mismatch must stop reading that destination.");
            Assert.AreEqual(0L, reads[2]);
            await File.WriteAllBytesAsync(source, []);
            await File.WriteAllBytesAsync(same, []);
            result = await ExistingContentComparer.CompareAsync(
                source, [same], 0, CancellationToken.None, _ => ValueTask.CompletedTask, (_, _) => { });
            Assert.IsTrue(result[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationDrainsReadsAndReleasesFileHandles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"comparison-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            await File.WriteAllBytesAsync(source, new byte[6 * 1024 * 1024]);
            File.Copy(source, target);
            using var cancel = new CancellationTokenSource();
            try
            {
                await ExistingContentComparer.CompareAsync(source, [target], 6 * 1024 * 1024,
                    cancel.Token, _ => ValueTask.CompletedTask, (_, _) => cancel.Cancel());
                Assert.Fail("Cancellation must interrupt comparison.");
            }
            catch (OperationCanceledException) { }
            using var exclusiveSource = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var exclusiveTarget = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LogicalClassificationAndPhysicalReadsAreDistinctFromCopy()
    {
        var progress = new DestinationProgress("D:\\ISOS", 100, 1);
        progress.BeginComparison(100);
        progress.SetLastFile("test.iso");
        progress.AddCompared(20);
        var reading = progress.Snapshot();
        Assert.AreEqual(DestinationPhase.Comparing, reading.Phase);
        Assert.AreEqual(20UL, reading.ComparisonBytesRead);
        Assert.AreEqual(20UL, reading.ComparisonBytesProcessed);
        Assert.AreEqual("Compar. 20%", DestinationProgressText.Format(reading));
        Assert.AreEqual(0UL, reading.Written);
        Assert.IsNull(reading.CopyStartedAt);
        progress.MarkComparisonFileDone(100, false);
        var classified = progress.Snapshot();
        Assert.AreEqual(100UL, classified.ComparisonBytesProcessed);
        Assert.AreEqual(20UL, classified.ComparisonBytesRead);
        Assert.AreEqual(0UL, classified.ComparisonIdenticalFiles);
        Assert.AreEqual(1UL, classified.ComparisonFilesDone);
        progress.SetPhase(DestinationPhase.Copying);
        Assert.IsNotNull(progress.Snapshot().CopyStartedAt);
    }
}
