from pathlib import Path

engine = Path("dotnet/RepartoCopier.Core/CopyEngine.cs")
text = engine.read_text(encoding="utf-8")
start = text.index("    private static PreparedCopy Preflight(CopyPlan plan)\n")
end = text.index("    private static async Task RunAsync(\n", start)
new_preflight = '''    private static PreparedCopy Preflight(CopyPlan plan)
    {
        var requestedSource = Path.GetFullPath(plan.Source);
        var sourceIsDirectory = Directory.Exists(requestedSource);
        var sourceIsFile = File.Exists(requestedSource);
        if (!sourceIsDirectory && !sourceIsFile)
            throw new IOException("El origen debe ser un archivo regular o una carpeta existente.");
        if (WindowsPath.IsReparsePoint(requestedSource))
            throw new IOException("El origen no puede ser un symlink/junction/reparse point.");

        var source = PreflightSafety.CanonicalExisting(requestedSource, "origen");
        var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new IOException("El origen debe tener un nombre; no se puede duplicar una raíz completa.");

        var effectiveDestinations = plan.Destinations
            .Select(Path.GetFullPath)
            .Select(basePath => sourceIsDirectory ? Path.Combine(basePath, sourceName) : basePath)
            .ToArray();
        var destinationRoots = PreflightSafety.ValidateAndCanonicalizeDestinations(
            source,
            effectiveDestinations);

        var sourceRoot = sourceIsDirectory
            ? source
            : Path.GetDirectoryName(source)
                ?? throw new IOException("El archivo de origen no tiene carpeta padre.");

        SourceTreeScan scan;
        if (sourceIsDirectory)
        {
            scan = PreflightSafety.ScanDirectory(source);
        }
        else
        {
            RejectReparse(source, "archivo de origen");
            var info = new FileInfo(source);
            scan = new SourceTreeScan(
                [new ScannedFile(
                    source,
                    Path.GetFileName(source),
                    info.Length,
                    info.LastWriteTimeUtc)],
                []);
        }

        var files = scan.Files
            .Select(file => new FileEntry(
                file.FullPath,
                file.RelativePath,
                file.Size,
                file.LastWriteTimeUtc,
                ToUnixNanoseconds(file.LastWriteTimeUtc)))
            .ToList();
        var directories = scan.Directories.ToList();
        var totalBytes = files.Aggregate<FileEntry, ulong>(
            0,
            (sum, file) => checked(sum + (ulong)file.Size));

        foreach (var root in destinationRoots)
        {
            PreflightSafety.ValidateDestinationLayout(root, directories, scan.Files);
            PreflightSafety.EnsureFreeSpace(root, scan.Files);
            StateLayout.PrepareTempDirectory(root);
            foreach (var relative in directories)
                EnsureDestinationDirectory(root, relative);
        }

        return new PreparedCopy(sourceRoot, destinationRoots, files, directories, totalBytes);
    }

'''
text = text[:start] + new_preflight + text[end:]
engine.write_text(text, encoding="utf-8")

safety = Path("dotnet/RepartoCopier.Core/PreflightSafety.cs")
text = safety.read_text(encoding="utf-8")
old = '''        foreach (var requested in effectiveDestinations)
        {
            var full = Path.GetFullPath(requested);
            Directory.CreateDirectory(full);
'''
new = '''        foreach (var requested in effectiveDestinations)
        {
            var full = Path.GetFullPath(requested);
            if (PathsOverlap(source, full))
                throw new IOException($"El destino {requested} se solapa con el origen.");
            Directory.CreateDirectory(full);
'''
if text.count(old) != 1:
    raise SystemExit("expected one destination lexical-overlap anchor")
text = text.replace(old, new, 1)
safety.write_text(text, encoding="utf-8")

tests = Path("dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs")
text = tests.read_text(encoding="utf-8")
anchor = '''    [TestMethod]
    public async Task FanOutDeliversMultipleBlocksToEveryDestination()'''
if anchor not in text:
    raise SystemExit("expected FAN-OUT multiblock test anchor")
insert = r'''    [TestMethod]
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

'''
text = text.replace(anchor, insert + anchor, 1)
tests.write_text(text, encoding="utf-8")
