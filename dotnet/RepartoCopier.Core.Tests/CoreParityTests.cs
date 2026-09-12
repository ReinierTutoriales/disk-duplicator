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
        Assert.ThrowsException<ArgumentException>(() =>
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

        Assert.AreEqual("solo este archivo", await File.ReadAllTextAsync(Path.Combine(destination, "elegido.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "hermano.txt")));
        Assert.AreEqual(DestinationPhase.Done, job.Snapshot().Single().Phase);
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
