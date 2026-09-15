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
        CleanupOwnedStaleFiles(destinationRoot, files);
        RecoverCompletedRewrite(destinationRoot);
        var valid = NormalizeCompletedState(destinationRoot, files);
'@ @'
        CleanupOwnedStaleFiles(destinationRoot, files);
        RecoverCompletedRewrite(destinationRoot);
        RecoverManifestRewrite(destinationRoot);
        var valid = NormalizeCompletedState(destinationRoot, files);
'@

Replace-Exact $recovery @'
    internal static void CompactManifest(
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
    {
        var path = StateLayout.ManifestPath(destinationRoot);
'@ @'
    internal static void CompactManifest(
        string destinationRoot,
        IReadOnlyList<RecoveryFile> files)
    {
        RecoverManifestRewrite(destinationRoot);
        var path = StateLayout.ManifestPath(destinationRoot);
'@

Replace-Exact $recovery @'
        var tmp = Path.ChangeExtension(path, "b3.compact");
        var backup = Path.ChangeExtension(path, "b3.compact.bak");
'@ @'
        var tmp = ManifestRewriteTempPath(destinationRoot);
        var backup = ManifestRewriteBackupPath(destinationRoot);
'@

Replace-Exact $recovery @'
    private static HashSet<string> NormalizeCompletedState(
'@ @'
    internal static void RecoverManifestRewrite(string destinationRoot)
    {
        var path = StateLayout.ManifestPath(destinationRoot);
        var tmp = ManifestRewriteTempPath(destinationRoot);
        var backup = ManifestRewriteBackupPath(destinationRoot);

        if (File.Exists(path) || Directory.Exists(path))
        {
            EnsureOwnedRegularFile(path, "manifest");
            DeleteOwnedFileIfPresent(backup, "backup de manifest");
            DeleteOwnedFileIfPresent(tmp, "temporal de manifest");
            return;
        }

        if (File.Exists(backup) || Directory.Exists(backup))
        {
            EnsureOwnedRegularFile(backup, "backup de manifest");
            File.Move(backup, path);
        }
        DeleteOwnedFileIfPresent(tmp, "temporal de manifest");
    }

    private static HashSet<string> NormalizeCompletedState(
'@

Replace-Exact $recovery @'
    private static string StateRewriteBackupPath(string destinationRoot) =>
        Path.Combine(StateLayout.StateDirectoryFor(destinationRoot), "completed.jsonl.preflight.bak");

    private static string PreviousStateId(string path) =>
'@ @'
    private static string StateRewriteBackupPath(string destinationRoot) =>
        Path.Combine(StateLayout.StateDirectoryFor(destinationRoot), "completed.jsonl.preflight.bak");

    private static string ManifestRewriteTempPath(string destinationRoot) =>
        Path.ChangeExtension(StateLayout.ManifestPath(destinationRoot), "b3.compact");

    private static string ManifestRewriteBackupPath(string destinationRoot) =>
        Path.ChangeExtension(StateLayout.ManifestPath(destinationRoot), "b3.compact.bak");

    private static string PreviousStateId(string path) =>
'@

$coreTests = 'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs'
Replace-Exact $coreTests @'
    private static RecoveryFile RecoveryFileFor(string sourcePath, string relativePath)
'@ @'
    [TestMethod]
    public void RecoveryRestoresManifestBackupBeforeNormalizingCompletedState()
    {
        using var temp = new TempDirectory("recovery-manifest-crash");
        var sourceRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var source = Path.Combine(sourceRoot, "a.bin");
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("abc"));
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        File.Copy(source, Path.Combine(destination, "a.bin"));
        var file = RecoveryFileFor(source, "a.bin");
        var hash = Hasher.Hash(File.ReadAllBytes(source)).AsSpan().ToArray();
        WriteLegacyRecoveryProof(destination, file, hash);

        var manifest = StateLayout.ManifestPath(destination);
        var compactTemp = Path.ChangeExtension(manifest, "b3.compact");
        var compactBackup = Path.ChangeExtension(manifest, "b3.compact.bak");
        File.Move(manifest, compactBackup);
        File.WriteAllText(compactTemp, "partial-new-manifest");

        var valid = RecoveryManager.PrepareAndNormalize(sourceRoot, destination, [file]);

        Assert.IsTrue(valid.Contains(RecoveryManager.StateKey(file)));
        Assert.IsTrue(File.Exists(manifest));
        Assert.IsFalse(File.Exists(compactBackup));
        Assert.IsFalse(File.Exists(compactTemp));
        StringAssert.Contains(File.ReadAllText(manifest), RecoveryManager.ManifestKey(file.RelativePath));
        StringAssert.Contains(File.ReadAllText(StateLayout.JournalPath(destination)), RecoveryManager.StateKey(file));
    }

    private static RecoveryFile RecoveryFileFor(string sourcePath, string relativePath)
'@

$contract = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
Replace-Exact $contract @'
    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@ @'
    [TestMethod]
    public void RecoveryOwnsManifestRewriteRecoveryRoute()
    {
        var methods = typeof(RecoveryManager)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToArray();
        CollectionAssert.Contains(methods, "RecoverManifestRewrite");
        CollectionAssert.Contains(methods, "CompactManifest");
        CollectionAssert.Contains(methods, "PrepareAndNormalize");
    }

    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@

Write-Host 'Manifest recovery patch applied.'
