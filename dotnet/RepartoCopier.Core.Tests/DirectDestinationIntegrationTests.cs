using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class DirectDestinationIntegrationTests
{
    [TestMethod]
    public async Task ProductionCopyReportsDirectDestinationActivationOrExplicitFallback()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const int size = 16 * 1024 * 1024 + 193;
        var root = Path.Combine(Path.GetTempPath(), $"repartocopier-direct-integration-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        var destinationRoot = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);
        var source = Path.Combine(sourceRoot, "payload.bin");
        var destination = Path.Combine(destinationRoot, "payload.bin");

        try
        {
            var expected = new byte[size];
            new Random(2026091402).NextBytes(expected);
            await File.WriteAllBytesAsync(source, expected);

            var topology = StorageTopology.InspectDestinations([destinationRoot]).Destinations.Single();
            var eligible = DirectIoDestinationWriter.IsEligible(topology, size);

            var plan = CopyPlan.Create(source, [destinationRoot], skipSame: false, keepGoing: false);
            await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: false, SkipSame: false, KeepGoing: false));
            await job.Completion.WaitAsync(TimeSpan.FromMinutes(2));

            Assert.IsTrue(File.Exists(destination));
            var actual = await File.ReadAllBytesAsync(destination);
            Assert.AreEqual(expected.LongLength, actual.LongLength);
            CollectionAssert.AreEqual(expected, actual);

            var diagnostics = job.DiagnosticsSnapshot();
            if (eligible)
            {
                Assert.IsTrue(
                    diagnostics.DirectDestinationFiles > 0 || diagnostics.DirectDestinationFallbacks > 0,
                    "Un destino elegible para Direct I/O debe registrar activación o fallback explícito en la ruta de producción.");
            }

            if (diagnostics.DirectDestinationWriteBytes > 0)
            {
                Assert.AreEqual(size, diagnostics.DirectDestinationWriteBytes);
                Assert.IsGreaterThan(0L, diagnostics.DirectDestinationWriteOperations);
                Assert.IsGreaterThan(0, diagnostics.DirectDestinationFiles);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
