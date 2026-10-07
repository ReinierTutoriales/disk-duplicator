using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;
using RepartoCopier.WinUI;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class OptionalVerificationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProfileRoundTripPreservesVerificationChoice(bool verify)
    {
        var profile = new CopyProfile(1, @"C:\Source", [@"D:\Backup"], false, false, verify);
        var restored = CopyProfileSerializer.Deserialize(CopyProfileSerializer.Serialize(profile));
        Assert.AreEqual(verify, restored.VerifyAfterCopy);
    }

    [TestMethod]
    public void LegacyProfileWithoutVerificationFieldKeepsFullVerification()
    {
        var restored = CopyProfileSerializer.Deserialize(
            """{"version":1,"source":"C:\\Source","destinations":["D:\\Backup"],"skipExisting":false,"continueOnError":false,"shutdownWhenFinished":false}""");
        Assert.IsTrue(restored.VerifyAfterCopy);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EngineOmitsOnlyFinalVerificationWhenNotRequested(bool verify)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows storage and Direct I/O.");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), $"RepartoCopier-optional-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "Source")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(root, "Destination")).FullName;
            // Exercise a non-aligned tail, and inspect the committed contents independently.
            var payload = new byte[3 * 1024 * 1024 + 137];
            new Random(41).NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
            var plan = CopyPlan.Create(source, [destination], existingFiles: ExistingFilePolicy.ReplaceAll, keepGoing: false);
            await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: verify));
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            var snapshot = job.Snapshot().Single();
            var diagnostics = job.DiagnosticsSnapshot();
            Assert.AreEqual(DestinationPhase.Done, snapshot.Phase);
            Assert.AreEqual(0UL, snapshot.FilesErrored);
            Assert.IsNotNull(snapshot.CopyFinishedAt);
            Assert.AreEqual((long)payload.Length, diagnostics.SourceHashBytes);
            Assert.AreEqual(1UL, snapshot.DurableFlushes);
            var committed = Path.Combine(destination, "Source", "payload.bin");
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(committed));
            if (verify)
            {
                Assert.IsNotNull(snapshot.VerifyFinishedAt);
                Assert.AreEqual((ulong)payload.Length, snapshot.VerifiedBytes);
                Assert.AreEqual("Verificado", DestinationProgressText.Format(snapshot));
                Assert.AreEqual("Completada", DestinationProgressText.Verification(snapshot));
                Assert.IsTrue(diagnostics.VerifyReadBytes > 0);
            }
            else
            {
                Assert.IsNull(snapshot.VerifyStartedAt);
                Assert.IsNull(snapshot.VerifyFinishedAt);
                Assert.AreEqual(0UL, snapshot.VerifiedBytes);
                Assert.AreEqual(0L, diagnostics.VerifyReadBytes);
                Assert.AreEqual(TimeSpan.Zero, diagnostics.VerifyPhaseElapsed);
                Assert.AreEqual("Copiado", DestinationProgressText.Format(snapshot));
                Assert.AreEqual("No realizada", DestinationProgressText.Verification(snapshot));
            }
            using var document = JsonDocument.Parse(DiagnosticsExport.Serialize(DiagnosticsExport.Create(
                "2.1.1.0", null, null, diagnostics, [snapshot], verify)));
            Assert.AreEqual(verify, document.RootElement.GetProperty("VerificationRequested").GetBoolean());
            Assert.AreEqual(verify, document.RootElement.GetProperty("Destinations")[0]
                .GetProperty("VerifyFinishedAt").ValueKind != JsonValueKind.Null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
