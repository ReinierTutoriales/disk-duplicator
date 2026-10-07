using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class PreparationCancellationTests
{
    [TestMethod]
    public async Task CancelledStartDoesNotCreateDestinationOrState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.bin");
            File.WriteAllBytes(source, [1, 2, 3]);
            var destination = Path.Combine(root, "destination");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await CopyEngine.StartAsync(CopyPlan.Create(source, [destination], ExistingFilePolicy.ReplaceAll, false),
                    cancellationToken: cancellation.Token));
            Assert.IsFalse(Directory.Exists(destination));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void CancelledScanStopsBeforeAccessingSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            PreflightSafety.ScanDirectory("missing-source", cancellation.Token));
    }

    [TestMethod]
    public void CancelledRecoveryDoesNotChangeCheckpointOrDestination()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            RecoveryManager.PrepareAndNormalize("missing-source", "missing-destination", [], cancellation.Token));
    }
}
