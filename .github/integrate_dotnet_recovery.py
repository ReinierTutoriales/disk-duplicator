from pathlib import Path

engine = Path("dotnet/RepartoCopier.Core/CopyEngine.cs")
text = engine.read_text(encoding="utf-8")

old = '''        foreach (var root in destinationRoots)
        {
            PreflightSafety.ValidateDestinationLayout(root, directories, scan.Files);
            PreflightSafety.EnsureFreeSpace(root, scan.Files);
            StateLayout.PrepareTempDirectory(root);
            foreach (var relative in directories)
                EnsureDestinationDirectory(root, relative);
        }

        return new PreparedCopy(sourceRoot, destinationRoots, files, directories, totalBytes);
'''
new = '''        var recoveryFiles = files
            .Select(file => new RecoveryFile(
                file.SourcePath,
                file.RelativePath,
                file.Size,
                file.ModifiedUnixNanoseconds))
            .ToArray();
        var preverifiedSkips = CreateEmptySkipMasks(files.Count, destinationRoots.Length);
        for (var slot = 0; slot < destinationRoots.Length; slot++)
        {
            var root = destinationRoots[slot];
            PreflightSafety.ValidateDestinationLayout(root, directories, scan.Files);
            var completed = RecoveryManager.PrepareAndNormalize(root, recoveryFiles);
            var skippedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                if (!completed.Contains(RecoveryManager.StateKey(recoveryFiles[fileIndex])))
                    continue;
                preverifiedSkips[fileIndex][slot] = true;
                skippedPaths.Add(files[fileIndex].RelativePath);
            }
            PreflightSafety.EnsureFreeSpace(root, scan.Files, skippedPaths);
            foreach (var relative in directories)
                EnsureDestinationDirectory(root, relative);
        }

        return new PreparedCopy(
            sourceRoot,
            destinationRoots,
            files,
            directories,
            totalBytes,
            preverifiedSkips);
'''
if text.count(old) != 1:
    raise SystemExit("expected one preflight destination planning anchor")
text = text.replace(old, new, 1)

old = '''            var skipMasks = options.SkipSame
                ? await BuildVerifiedSkipMasksAsync(copy, progress, job, token).ConfigureAwait(false)
                : CreateEmptySkipMasks(copy.Files.Count, copy.DestinationRoots.Length);
'''
new = '''            var skipMasks = options.SkipSame
                ? await BuildVerifiedSkipMasksAsync(copy, progress, job, token).ConfigureAwait(false)
                : CreateEmptySkipMasks(copy.Files.Count, copy.DestinationRoots.Length);
            for (var fileIndex = 0; fileIndex < copy.Files.Count; fileIndex++)
            {
                for (var slot = 0; slot < copy.DestinationRoots.Length; slot++)
                    skipMasks[fileIndex][slot] |= copy.PreverifiedSkips[fileIndex][slot];
            }
'''
if text.count(old) != 1:
    raise SystemExit("expected one skip mask anchor")
text = text.replace(old, new, 1)

old = '''    private static void AppendRecoveryState(string root, FileEntry entry, byte[] hash)
    {
        var journal = StateLayout.JournalPath(root);
        var manifest = StateLayout.ManifestPath(root);
        var key = $"{StateLayout.PersistedPathKey(entry.RelativePath)}|{entry.Size}|{entry.ModifiedUnixNanoseconds}";
        File.AppendAllText(journal, key + Environment.NewLine);
        File.AppendAllText(manifest, $"{StateLayout.PersistedPathKey(entry.RelativePath)} {Convert.ToHexString(hash).ToLowerInvariant()}{Environment.NewLine}");
    }
'''
new = '''    private static void AppendRecoveryState(string root, FileEntry entry, byte[] hash) =>
        RecoveryManager.AppendDurable(
            root,
            new RecoveryFile(
                entry.SourcePath,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedUnixNanoseconds),
            hash);
'''
if text.count(old) != 1:
    raise SystemExit("expected one AppendRecoveryState anchor")
text = text.replace(old, new, 1)

old = '''    private sealed record PreparedCopy(
        string SourceRoot,
        string[] DestinationRoots,
        IReadOnlyList<FileEntry> Files,
        IReadOnlyList<string> Directories,
        ulong TotalBytes);
'''
new = '''    private sealed record PreparedCopy(
        string SourceRoot,
        string[] DestinationRoots,
        IReadOnlyList<FileEntry> Files,
        IReadOnlyList<string> Directories,
        ulong TotalBytes,
        bool[][] PreverifiedSkips);
'''
if text.count(old) != 1:
    raise SystemExit("expected one PreparedCopy anchor")
text = text.replace(old, new, 1)
engine.write_text(text, encoding="utf-8")

safety = Path("dotnet/RepartoCopier.Core/PreflightSafety.cs")
text = safety.read_text(encoding="utf-8")
old = '''    internal static void EnsureFreeSpace(
        string destinationRoot,
        IReadOnlyList<ScannedFile> files)
    {
        if (files.Count == 0)
            return;

        var volume = WindowsNative.GetVolumeMetrics(destinationRoot);
        var granularity = Math.Max(1UL, volume.AllocationGranularity);
        Int128 committedDelta = 0;
        Int128 peakExtra = 0;

        foreach (var file in files)
        {
'''
new = '''    internal static void EnsureFreeSpace(
        string destinationRoot,
        IReadOnlyList<ScannedFile> files,
        IReadOnlySet<string>? skippedRelativePaths = null)
    {
        if (files.Count == 0)
            return;

        var volume = WindowsNative.GetVolumeMetrics(destinationRoot);
        var granularity = Math.Max(1UL, volume.AllocationGranularity);
        Int128 committedDelta = 0;
        Int128 peakExtra = 0;
        ulong bytesToWrite = 0;

        foreach (var file in files)
        {
            if (skippedRelativePaths?.Contains(file.RelativePath) == true)
                continue;
'''
if text.count(old) != 1:
    raise SystemExit("expected one EnsureFreeSpace signature anchor")
text = text.replace(old, new, 1)
old = '''            var newAllocation = RoundUp((ulong)file.Size, granularity);
            var duringTemporary = committedDelta + (Int128)newAllocation;
'''
new = '''            var newAllocation = RoundUp((ulong)file.Size, granularity);
            bytesToWrite = SaturatingAdd(bytesToWrite, (ulong)file.Size);
            var duringTemporary = committedDelta + (Int128)newAllocation;
'''
if text.count(old) != 1:
    raise SystemExit("expected one allocation anchor")
text = text.replace(old, new, 1)
old = '''        var reserve = ReserveForVolume(volume.TotalBytes);
        var required = SaturatingAdd(peak, reserve);
'''
new = '''        var reserve = bytesToWrite == 0 ? 0UL : ReserveForVolume(volume.TotalBytes);
        var required = SaturatingAdd(peak, reserve);
'''
if text.count(old) != 1:
    raise SystemExit("expected one reserve anchor")
text = text.replace(old, new, 1)
safety.write_text(text, encoding="utf-8")

tests = Path("dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs")
text = tests.read_text(encoding="utf-8")
if "using Blake3;" not in text:
    text = text.replace("using System.Text;\n", "using System.Text;\nusing Blake3;\n", 1)

anchor = '''    [TestMethod]
    public async Task FanOutDeliversMultipleBlocksToEveryDestination()'''
if anchor not in text:
    raise SystemExit("expected multiblock test anchor")
insert = r'''    [TestMethod]
    public void RecoveryWritesRustCompatibleJournalAndManifest()
    {
        using var temp = new TempDirectory("recovery-format");
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var file = RecoveryFileFor(source, "folder\\a.bin");
        var hash = Hasher.Hash(File.ReadAllBytes(source)).AsSpan().ToArray();

        RecoveryManager.AppendDurable(destination, file, hash);

        Assert.AreEqual(
            $"{{\"key\":\"{RecoveryManager.StateKey(file)}\"}}",
            File.ReadAllText(StateLayout.JournalPath(destination)).TrimEnd());
        Assert.AreEqual(
            $"{Convert.ToHexString(hash).ToLowerInvariant()}  {RecoveryManager.ManifestKey(file.RelativePath)}",
            File.ReadAllText(StateLayout.ManifestPath(destination)).TrimEnd());
    }

    [TestMethod]
    public void RecoveryAcceptsOnlyPhysicallyVerifiedRustState()
    {
        using var temp = new TempDirectory("recovery-proof");
        var sourceRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var source = Path.Combine(sourceRoot, "a.bin");
        File.WriteAllBytes(source, [10, 20, 30]);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        File.Copy(source, Path.Combine(destination, "a.bin"));
        var file = RecoveryFileFor(source, "a.bin");
        var hash = Hasher.Hash(File.ReadAllBytes(source)).AsSpan().ToArray();
        StateLayout.PrepareStateDirectory(destination);
        File.WriteAllText(
            StateLayout.JournalPath(destination),
            $"{{\"key\":\"{RecoveryManager.StateKey(file)}\"}}\n");
        File.WriteAllText(
            StateLayout.ManifestPath(destination),
            $"{Convert.ToHexString(hash).ToLowerInvariant()}  {RecoveryManager.ManifestKey(file.RelativePath)}\n");

        var valid = RecoveryManager.PrepareAndNormalize(destination, [file]);
        Assert.IsTrue(valid.Contains(RecoveryManager.StateKey(file)));
    }

    [TestMethod]
    public void RecoveryRejectsJournalWithoutManifestProof()
    {
        using var temp = new TempDirectory("recovery-no-manifest");
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        File.Copy(source, Path.Combine(destination, "a.bin"));
        var file = RecoveryFileFor(source, "a.bin");
        StateLayout.PrepareStateDirectory(destination);
        File.WriteAllText(
            StateLayout.JournalPath(destination),
            $"{{\"key\":\"{RecoveryManager.StateKey(file)}\"}}\n");

        var valid = RecoveryManager.PrepareAndNormalize(destination, [file]);
        Assert.AreEqual(0, valid.Count);
        Assert.AreEqual(string.Empty, File.ReadAllText(StateLayout.JournalPath(destination)));
    }

    [TestMethod]
    public void RecoveryRejectsSameSizeDestinationCorruption()
    {
        using var temp = new TempDirectory("recovery-corrupt-dest");
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("abc"));
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        File.WriteAllBytes(Path.Combine(destination, "a.bin"), Encoding.ASCII.GetBytes("xyz"));
        var file = RecoveryFileFor(source, "a.bin");
        WriteRustRecoveryProof(destination, file, Hasher.Hash(Encoding.ASCII.GetBytes("abc")).AsSpan().ToArray());

        var valid = RecoveryManager.PrepareAndNormalize(destination, [file]);
        Assert.AreEqual(0, valid.Count);
    }

    [TestMethod]
    public void RecoveryRejectsChangedSourceAgainstPersistedManifest()
    {
        using var temp = new TempDirectory("recovery-changed-source");
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("abc"));
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        File.WriteAllBytes(Path.Combine(destination, "a.bin"), Encoding.ASCII.GetBytes("abc"));
        var file = RecoveryFileFor(source, "a.bin");
        var oldHash = Hasher.Hash(Encoding.ASCII.GetBytes("abc")).AsSpan().ToArray();
        WriteRustRecoveryProof(destination, file, oldHash);
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("xyz"));

        var valid = RecoveryManager.PrepareAndNormalize(destination, [file]);
        Assert.AreEqual(0, valid.Count);
    }

    [TestMethod]
    public void RecoveryUpgradesLegacyKeysToLosslessP2Keys()
    {
        using var temp = new TempDirectory("recovery-legacy");
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("abc"));
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        File.Copy(source, Path.Combine(destination, "a.bin"));
        var file = RecoveryFileFor(source, "a.bin");
        var hash = Hasher.Hash(File.ReadAllBytes(source)).AsSpan().ToArray();
        StateLayout.PrepareStateDirectory(destination);
        File.WriteAllText(
            StateLayout.JournalPath(destination),
            $"{{\"key\":\"{RecoveryManager.LegacyStateKey(file)}\"}}\n");
        File.WriteAllText(
            StateLayout.ManifestPath(destination),
            $"{Convert.ToHexString(hash).ToLowerInvariant()}  {RecoveryManager.LegacyManifestKey(file.RelativePath)}\n");

        var valid = RecoveryManager.PrepareAndNormalize(destination, [file]);
        Assert.IsTrue(valid.Contains(RecoveryManager.StateKey(file)));
        var journal = File.ReadAllText(StateLayout.JournalPath(destination));
        StringAssert.Contains(journal, RecoveryManager.StateKey(file));
        Assert.IsFalse(journal.Contains(RecoveryManager.LegacyStateKey(file), StringComparison.Ordinal));
        var manifest = File.ReadAllText(StateLayout.ManifestPath(destination));
        StringAssert.Contains(manifest, RecoveryManager.ManifestKey(file.RelativePath));
    }

    [TestMethod]
    public void RecoveryRestoresOwnedBackupAfterInterruptedCommit()
    {
        using var temp = new TempDirectory("recovery-backup");
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("new"));
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var destinationFile = Path.Combine(destination, "a.bin");
        var file = RecoveryFileFor(source, "a.bin");
        StateLayout.PrepareTempDirectory(destination);
        File.WriteAllBytes(StateLayout.BackupPath(destination, destinationFile), Encoding.ASCII.GetBytes("old"));

        RecoveryManager.PrepareAndNormalize(destination, [file]);
        Assert.AreEqual("old", Encoding.ASCII.GetString(File.ReadAllBytes(destinationFile)));
    }

    [TestMethod]
    public void RecoveryRejectsBackupDirectoryInsteadOfDeletingIt()
    {
        using var temp = new TempDirectory("recovery-unsafe-backup");
        var source = Path.Combine(temp.Path, "source.bin");
        File.WriteAllBytes(source, [1]);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var destinationFile = Path.Combine(destination, "a.bin");
        var file = RecoveryFileFor(source, "a.bin");
        StateLayout.PrepareTempDirectory(destination);
        Directory.CreateDirectory(StateLayout.BackupPath(destination, destinationFile));

        var error = Assert.ThrowsExactly<IOException>(() =>
            RecoveryManager.PrepareAndNormalize(destination, [file]));
        StringAssert.Contains(error.Message, "Entrada de estado no segura");
    }

'''
text = text.replace(anchor, insert + anchor, 1)

helper_anchor = '''    private static void AssertHealthy(CopyJob job)'''
if helper_anchor not in text:
    raise SystemExit("expected helper anchor")
helpers = r'''    private static RecoveryFile RecoveryFileFor(string sourcePath, string relativePath)
    {
        var info = new FileInfo(sourcePath);
        var ns = checked((info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100L);
        return new RecoveryFile(sourcePath, relativePath, info.Length, ns);
    }

    private static void WriteRustRecoveryProof(string destination, RecoveryFile file, byte[] hash)
    {
        StateLayout.PrepareStateDirectory(destination);
        File.WriteAllText(
            StateLayout.JournalPath(destination),
            $"{{\"key\":\"{RecoveryManager.StateKey(file)}\"}}\n");
        File.WriteAllText(
            StateLayout.ManifestPath(destination),
            $"{Convert.ToHexString(hash).ToLowerInvariant()}  {RecoveryManager.ManifestKey(file.RelativePath)}\n");
    }

'''
text = text.replace(helper_anchor, helpers + helper_anchor, 1)
tests.write_text(text, encoding="utf-8")
