using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class CoreParityTests
{
    [TestMethod]
    public void CopyPlanPreservesWindowsIdentityRulesAndLimit()
    {
        Assert.IsTrue(WindowsPath.SamePath(@"\\?\C:\Datos\", @"c:/datos"));
        Assert.IsTrue(WindowsPath.SamePath(@"\\?\UNC\Servidor\Share\", @"\\servidor\share"));

        var plan = CopyPlan.Create(
            " C:/Origen/ ",
            [" D:/Uno/ ", "E:/Dos"],
            skipSame: true,
            keepGoing: false);
        Assert.AreEqual("C:/Origen/", plan.Source);
        CollectionAssert.AreEqual(new[] { "D:/Uno/", "E:/Dos" }, plan.Destinations.ToArray());
        Assert.ThrowsExactly<ArgumentException>(() =>
            CopyPlan.Create("C:/Origen", ["C:/Origen/"], true, true));

        var existing = Enumerable.Range(0, CopyPlan.MaxDestinations - 1)
            .Select(index => $@"D:\Dest{index}")
            .ToList();
        var added = CopyPlan.AppendUniqueDestinations(
            @"C:\Source",
            existing,
            [@"E:\One", @"F:\Two"]);
        Assert.AreEqual(1, added);
        Assert.AreEqual(CopyPlan.MaxDestinations, existing.Count);
    }

    [TestMethod]
    public void SessionFormatRoundTripsUnicodeAndUnc()
    {
        var original = CopyPlan.Create(
            @"C:\Música\Niño\日本語",
            [@"D:\Copias", @"\\servidor\Datos compartidos"],
            skipSame: true,
            keepGoing: false);
        var text = SessionStore.Render(original);
        var loaded = SessionStore.Parse(text);

        Assert.AreEqual(original.Source, loaded.Source);
        CollectionAssert.AreEqual(original.Destinations.ToArray(), loaded.Destinations.ToArray());
        Assert.AreEqual(original.SkipSame, loaded.SkipSame);
        Assert.AreEqual(original.KeepGoing, loaded.KeepGoing);
        Assert.IsFalse(text.Contains("Música", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AtomicStorageReplacesWithoutLeavingSiblings()
    {
        using var temp = new TempDirectory("storage");
        var path = Path.Combine(temp.Path, "settings.conf");
        File.WriteAllText(path, "old");
        AtomicStorage.Write(path, "new"u8, "prueba");
        Assert.AreEqual("new", File.ReadAllText(path));
        Assert.AreEqual(1, Directory.EnumerateFileSystemEntries(temp.Path).Count());
    }

    [TestMethod]
    public void PreflightRejectsSingleFileDestinationContainingSource()
    {
        using var temp = new TempDirectory("preflight-file-overlap");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var source = Path.Combine(sourceDirectory, "selected.bin");
        File.WriteAllBytes(source, [1, 2, 3]);

        var plan = CopyPlan.Create(source, [sourceDirectory], skipSame: false, keepGoing: false);
        var error = Assert.ThrowsExactly<IOException>(() => CopyEngine.Start(plan));
        StringAssert.Contains(error.Message, "solapa");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
    }

    [TestMethod]
    public void PreflightRejectsDestinationInsideSourceBeforeCreatingEffectiveRoot()
    {
        using var temp = new TempDirectory("preflight-source-overlap");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Proyecto")).FullName;
        File.WriteAllText(Path.Combine(source, "data.txt"), "safe");
        var baseInsideSource = Path.Combine(source, "backup");
        var forbiddenEffectiveRoot = Path.Combine(baseInsideSource, "Proyecto");

        var plan = CopyPlan.Create(source, [baseInsideSource], skipSame: false, keepGoing: false);
        var error = Assert.ThrowsExactly<IOException>(() => CopyEngine.Start(plan));
        StringAssert.Contains(error.Message, "solapa");
        Assert.IsFalse(Directory.Exists(forbiddenEffectiveRoot));
    }

    [TestMethod]
    public void PreflightRejectsOverlappingDestinations()
    {
        using var temp = new TempDirectory("preflight-destination-overlap");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        var firstBase = Path.Combine(temp.Path, "dest");
        var secondBase = Path.Combine(firstBase, "Origen", "nested");

        var plan = CopyPlan.Create(source, [firstBase, secondBase], skipSame: false, keepGoing: false);
        var error = Assert.ThrowsExactly<IOException>(() => CopyEngine.Start(plan));
        StringAssert.Contains(error.Message, "solapan entre sí");
    }

    [TestMethod]
    public void PreflightRejectsDestinationFileDirectoryConflict()
    {
        using var temp = new TempDirectory("preflight-layout-conflict");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var effectiveRoot = Directory.CreateDirectory(Path.Combine(destinationBase, "Origen")).FullName;
        Directory.CreateDirectory(Path.Combine(effectiveRoot, "a.txt"));

        var plan = CopyPlan.Create(source, [destinationBase], skipSame: false, keepGoing: false);
        var error = Assert.ThrowsExactly<IOException>(() => CopyEngine.Start(plan));
        StringAssert.Contains(error.Message, "requiere un archivo");
    }

    [TestMethod]
    public void PreflightSpaceMathMatchesRustRules()
    {
        Assert.AreEqual(0UL, PreflightSafety.RoundUp(0, 4096));
        Assert.AreEqual(4096UL, PreflightSafety.RoundUp(1, 4096));
        Assert.AreEqual(8192UL, PreflightSafety.RoundUp(4097, 4096));
        Assert.AreEqual(1UL * 1024 * 1024 * 1024,
            PreflightSafety.ReserveForVolume(10UL * 1024 * 1024 * 1024));
        Assert.AreEqual(16UL * 1024 * 1024 * 1024,
            PreflightSafety.ReserveForVolume(100UL * 1024 * 1024 * 1024 * 1024));
    }

    [TestMethod]
    public async Task FanOutDeliversMultipleBlocksToEveryDestination()
    {
        using var temp = new TempDirectory("fanout-multiblock");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[20 * 1024 * 1024 + 137];
        new Random(12345).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "multi.bin"), payload);
        var destinations = new[]
        {
            Directory.CreateDirectory(Path.Combine(temp.Path, "dest-1")).FullName,
            Directory.CreateDirectory(Path.Combine(temp.Path, "dest-2")).FullName,
        };

        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: true);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        AssertHealthy(job);

        foreach (var destination in destinations)
        {
            var copied = await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "multi.bin"));
            CollectionAssert.AreEqual(payload, copied);
        }
    }

    [TestMethod]
    public async Task FanOutPreservesRootTreeEmptyDirectoriesAndBytes()
    {
        using var temp = new TempDirectory("fanout-tree");
        var sourceParent = Directory.CreateDirectory(Path.Combine(temp.Path, "source-parent")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(sourceParent, "Proyecto")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "vacía"));
        Directory.CreateDirectory(Path.Combine(source, "Nivel1", "Nivel2"));
        await File.WriteAllTextAsync(Path.Combine(source, "raíz.txt"), "hola árbol", Encoding.UTF8);
        await File.WriteAllBytesAsync(
            Path.Combine(source, "Nivel1", "Nivel2", "datos.bin"),
            Enumerable.Range(0, 512 * 1024).Select(index => (byte)(index % 251)).ToArray());

        var baseOne = Directory.CreateDirectory(Path.Combine(temp.Path, "dest-1")).FullName;
        var baseTwo = Directory.CreateDirectory(Path.Combine(temp.Path, "dest-2")).FullName;
        var plan = CopyPlan.Create(source, [baseOne, baseTwo], skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        AssertHealthy(job);

        foreach (var destinationBase in new[] { baseOne, baseTwo })
        {
            var copiedRoot = Path.Combine(destinationBase, "Proyecto");
            Assert.IsTrue(Directory.Exists(copiedRoot));
            Assert.IsTrue(Directory.Exists(Path.Combine(copiedRoot, "vacía")));
            Assert.IsTrue(Directory.Exists(Path.Combine(copiedRoot, "Nivel1", "Nivel2")));
            CollectionAssert.AreEqual(
                await File.ReadAllBytesAsync(Path.Combine(source, "raíz.txt")),
                await File.ReadAllBytesAsync(Path.Combine(copiedRoot, "raíz.txt")));
            CollectionAssert.AreEqual(
                await File.ReadAllBytesAsync(Path.Combine(source, "Nivel1", "Nivel2", "datos.bin")),
                await File.ReadAllBytesAsync(Path.Combine(copiedRoot, "Nivel1", "Nivel2", "datos.bin")));
            Assert.IsFalse(Directory.Exists(Path.Combine(copiedRoot, ".disk-duplicator-state")));
        }

        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));
    }

    [TestMethod]
    public async Task SingleFileCopiesOnlyThatFile()
    {
        using var temp = new TempDirectory("single-file");
        var sourceDir = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var source = Path.Combine(sourceDir, "elegido.txt");
        await File.WriteAllTextAsync(source, "solo este archivo");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "hermano.txt"), "no copiar");
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;

        var plan = CopyPlan.Create(source, [destination], skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        AssertHealthy(job);

        Assert.AreEqual("solo este archivo", await File.ReadAllTextAsync(Path.Combine(destination, "elegido.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "hermano.txt")));
        Assert.AreEqual(DestinationPhase.Done, job.Snapshot().Single().Phase);
    }

    private static void AssertHealthy(CopyJob job)
    {
        var snapshots = job.Snapshot();
        var bad = snapshots.Where(item => item.Phase != DestinationPhase.Done).ToArray();
        if (bad.Length == 0) return;
        Assert.Fail(string.Join(" | ", bad.Select(item => $"{item.Label}: {item.Phase}: {item.Error}")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string name)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"repartocopier-dotnet-{name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
