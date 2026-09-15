$ErrorActionPreference = 'Stop'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText((Resolve-Path $Path)) }
function Write-Text([string]$Path, [string]$Content) { [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, [System.Text.UTF8Encoding]::new($false)) }
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if (-not $text.Contains($Old)) { throw "Expected anchor missing in $Path`n$Old" }
    Write-Text $Path ($text.Replace($Old, $New))
}

$recovery = 'dotnet/RepartoCopier.Core/Recovery.cs'
Replace-Exact $recovery @'
        StateLayout.PrepareTempDirectory(destinationRoot);
        CleanupOwnedStaleFiles(destinationRoot, files);
        RecoverCompletedRewrite(destinationRoot);
'@ @'
        StateLayout.PrepareTempDirectory(destinationRoot);
        CleanupOwnedStaleFiles(destinationRoot, files);
        CleanupOwnedOrphanParts(destinationRoot);
        RecoverCompletedRewrite(destinationRoot);
'@

Replace-Exact $recovery @'
    private static IEnumerable<string> PartCandidates(string root, string destination)
'@ @'
    internal static void CleanupOwnedOrphanParts(string destinationRoot)
    {
        var tmp = Path.Combine(StateLayout.StateDirectoryFor(destinationRoot), "tmp");
        if (!Directory.Exists(tmp))
            return;
        WindowsPath.EnsureNormalDirectory(tmp, "El directorio temporal de estado");

        foreach (var entry in Directory.EnumerateFileSystemEntries(tmp))
        {
            var name = Path.GetFileName(entry);
            if (!IsOwnedTransientPartName(name))
                continue;
            EnsureOwnedRegularFile(entry, "temporal huérfano");
            File.Delete(entry);
        }
    }

    internal static bool IsOwnedTransientPartName(string name)
    {
        if (!name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = name[..^5];
        if (stem.Length is not (24 or 32))
            return false;
        foreach (var ch in stem)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }
        return true;
    }

    private static IEnumerable<string> PartCandidates(string root, string destination)
'@

$tests = 'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs'
Replace-Exact $tests @'
    [TestMethod]
    public void RecoveryRestoresManifestBackupBeforeNormalizingCompletedState()
'@ @'
    [TestMethod]
    public void RecoveryDeletesOnlyOwnedOrphanPartsAndPreservesUnknownBackups()
    {
        using var temp = new TempDirectory("recovery-orphan-part");
        var sourceRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var tmp = StateLayout.PrepareTempDirectory(destination);
        var orphanPart = Path.Combine(tmp, $"{new string('a', 32)}.part");
        var legacyPart = Path.Combine(tmp, $"{new string('b', 24)}.part");
        var preservedBackup = Path.Combine(tmp, $"{new string('c', 32)}.bak");
        var foreignPart = Path.Combine(tmp, "not-owned.part");
        File.WriteAllText(orphanPart, "partial");
        File.WriteAllText(legacyPart, "partial-legacy");
        File.WriteAllText(preservedBackup, "recoverable");
        File.WriteAllText(foreignPart, "foreign");

        RecoveryManager.PrepareAndNormalize(sourceRoot, destination, []);

        Assert.IsFalse(File.Exists(orphanPart));
        Assert.IsFalse(File.Exists(legacyPart));
        Assert.IsTrue(File.Exists(preservedBackup), "Un .bak huérfano desconocido puede ser la única copia recuperable y debe preservarse.");
        Assert.IsTrue(File.Exists(foreignPart), "No se deben borrar temporales que no pertenecen inequívocamente al namespace de RepartoCopier.");
    }

    [TestMethod]
    public void RecoveryRejectsOwnedOrphanPartDirectoryInsteadOfDeletingIt()
    {
        using var temp = new TempDirectory("recovery-orphan-part-directory");
        var sourceRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var tmp = StateLayout.PrepareTempDirectory(destination);
        var unsafeEntry = Path.Combine(tmp, $"{new string('d', 32)}.part");
        Directory.CreateDirectory(unsafeEntry);

        var error = Assert.ThrowsExactly<IOException>(() =>
            RecoveryManager.PrepareAndNormalize(sourceRoot, destination, []));
        StringAssert.Contains(error.Message, "Entrada de estado no segura");
        Assert.IsTrue(Directory.Exists(unsafeEntry));
    }

    [TestMethod]
    public void RecoveryRestoresManifestBackupBeforeNormalizingCompletedState()
'@

$contract = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
Replace-Exact $contract @'
    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@ @'
    [TestMethod]
    public void RecoveryHasConservativeGlobalOrphanPartCleanup()
    {
        var cleanup = typeof(RecoveryManager).GetMethod(
            "CleanupOwnedOrphanParts",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("RecoveryManager.CleanupOwnedOrphanParts no existe.");
        var prepare = typeof(RecoveryManager).GetMethod(
            "PrepareAndNormalize",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("RecoveryManager.PrepareAndNormalize no existe.");
        Assert.IsTrue(MethodCalls(prepare, cleanup), "PrepareAndNormalize debe consumir el cleanup global de .part bajo el lease del destino.");

        var classifier = typeof(RecoveryManager).GetMethod(
            "IsOwnedTransientPartName",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("RecoveryManager.IsOwnedTransientPartName no existe.");
        Assert.AreEqual(true, classifier.Invoke(null, [$"{new string('a', 32)}.part"]));
        Assert.AreEqual(true, classifier.Invoke(null, [$"{new string('b', 24)}.part"]));
        Assert.AreEqual(false, classifier.Invoke(null, [$"{new string('c', 32)}.bak"]));
        Assert.AreEqual(false, classifier.Invoke(null, ["foreign.part"]));
    }

    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@

Write-Host 'Orphan .part cleanup patch applied.'
