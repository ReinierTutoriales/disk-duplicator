using System.Buffers;
using System.Text;
using Blake3;
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
    public void DenseDirectoryScanStreamsThousandsOfEntriesExactly()
    {
        using var temp = new TempDirectory("dense-streaming-scan");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        const int fileCount = 3000;
        for (var index = 0; index < fileCount; index++)
            File.WriteAllText(Path.Combine(source, $"file-{index:D4}.txt"), index.ToString());

        var scan = PreflightSafety.ScanDirectory(source);
        Assert.AreEqual(fileCount, scan.Files.Count);
        Assert.AreEqual(0, scan.Directories.Count);
        Assert.AreEqual("file-0000.txt", scan.Files[0].RelativePath);
        Assert.AreEqual($"file-{fileCount - 1:D4}.txt", scan.Files[^1].RelativePath);
    }

    [TestMethod]
    public void TransientPathsReuseOneIdentityWithoutChangingLayout()
    {
        using var temp = new TempDirectory("transient-paths");
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var destination = Path.Combine(root, "nested", "payload.bin");
        var pair = StateLayout.TransientPaths(root, destination);
        Assert.AreEqual(StateLayout.PartPath(root, destination), pair.PartPath);
        Assert.AreEqual(StateLayout.BackupPath(root, destination), pair.BackupPath);
    }

    [TestMethod]
    public void SourceTreeSnapshotDetectsStructuralAndMetadataMutation()
    {
        using var temp = new TempDirectory("source-tree-snapshot");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var file = Path.Combine(source, "a.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var baseline = PreflightSafety.ScanDirectory(source);

        File.WriteAllText(Path.Combine(source, "added.txt"), "new");
        Assert.ThrowsExactly<IOException>(() =>
            PreflightSafety.ValidateSourceTreeSnapshot(source, baseline));
        File.Delete(Path.Combine(source, "added.txt"));

        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.ThrowsExactly<IOException>(() =>
            PreflightSafety.ValidateSourceTreeSnapshot(source, baseline));
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
    public void RecoveryWritesLegacyCompatibleJournalAndManifest()
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
    public void RecoveryAcceptsOnlyPhysicallyVerifiedLegacyState()
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

        var valid = RecoveryManager.PrepareAndNormalize(Path.GetDirectoryName(source)!, destination, [file]);
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

        var valid = RecoveryManager.PrepareAndNormalize(Path.GetDirectoryName(source)!, destination, [file]);
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
        WriteLegacyRecoveryProof(destination, file, Hasher.Hash(Encoding.ASCII.GetBytes("abc")).AsSpan().ToArray());

        var valid = RecoveryManager.PrepareAndNormalize(Path.GetDirectoryName(source)!, destination, [file]);
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
        WriteLegacyRecoveryProof(destination, file, oldHash);
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("xyz"));

        var valid = RecoveryManager.PrepareAndNormalize(Path.GetDirectoryName(source)!, destination, [file]);
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

        var valid = RecoveryManager.PrepareAndNormalize(Path.GetDirectoryName(source)!, destination, [file]);
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

        RecoveryManager.PrepareAndNormalize(Path.GetDirectoryName(source)!, destination, [file]);
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
            RecoveryManager.PrepareAndNormalize(Path.GetDirectoryName(source)!, destination, [file]));
        StringAssert.Contains(error.Message, "Entrada de estado no segura");
    }

    [TestMethod]
    public async Task EngineFlushesBatchedRecoveryAtJobCompletion()
    {
        using var temp = new TempDirectory("recovery-batched-engine");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        for (var index = 0; index < 150; index++)
            await File.WriteAllTextAsync(Path.Combine(source, $"file-{index:D3}.txt"), $"payload-{index}");

        var destinationBase = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var plan = CopyPlan.Create(source, [destinationBase], skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        AssertHealthy(job);

        var root = Path.Combine(destinationBase, "Origen");
        Assert.AreEqual(150, File.ReadLines(StateLayout.JournalPath(root)).Count());
        Assert.AreEqual(150, File.ReadLines(StateLayout.ManifestPath(root)).Count());
    }

    [TestMethod]
    public async Task PipelineGovernorCancellationDoesNotLeakPrefetchCapacity()
    {
        var governor = new CopyEngine.PipelineGovernor();
        await governor.AcquirePrefetchSlotAsync(CancellationToken.None);
        await governor.AcquirePrefetchSlotAsync(CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        var blocked = governor.AcquirePrefetchSlotAsync(cancel.Token).AsTask();
        cancel.Cancel();
        try
        {
            await blocked;
            Assert.Fail("Se esperaba cancelación.");
        }
        catch (OperationCanceledException)
        {
        }

        governor.ReleasePrefetchSlot();
        await governor.AcquirePrefetchSlotAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, governor.InFlight);
        governor.ReleasePrefetchSlot();
        governor.ReleasePrefetchSlot();
        Assert.AreEqual(0, governor.InFlight);
    }

    [TestMethod]
    public async Task AdaptiveByteBudgetCancellationReturnsGrantedBytes()
    {
        var budget = new CopyEngine.AdaptiveByteBudget(64, 64);
        await budget.AcquireAsync(64, CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        var blocked = budget.AcquireAsync(64, cancel.Token).AsTask();
        cancel.Cancel();
        try
        {
            await blocked;
            Assert.Fail("Se esperaba cancelación.");
        }
        catch (OperationCanceledException)
        {
        }

        budget.Release(64);
        Assert.AreEqual(0L, budget.UsedBytes);
        await budget.AcquireAsync(64, CancellationToken.None);
        Assert.AreEqual(64L, budget.UsedBytes);
        budget.Release(64);
        Assert.AreEqual(0L, budget.UsedBytes);
    }

    [TestMethod]
    public void SharedBlockRejectsReferenceOverRelease()
    {
        var budget = new CopyEngine.AdaptiveByteBudget(64, 64);
        budget.AcquireAsync(64, CancellationToken.None).GetAwaiter().GetResult();
        var buffer = ArrayPool<byte>.Shared.Rent(64);
        var block = new CopyEngine.SharedBlock(buffer, 64, 64, 1, budget);
        block.Release();
        Assert.AreEqual(0L, budget.UsedBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() => block.Release());
    }

    [TestMethod]
    public async Task ResourceGovernorCancelledWaiterDoesNotConsumeCpuLease()
    {
        using var governor = new CopyEngine.ResourceGovernor();
        using var first = await governor.EnterCpuWorkAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var blocked = governor.EnterCpuWorkAsync(cancel.Token).AsTask();
        cancel.Cancel();
        try
        {
            await blocked;
            Assert.Fail("Se esperaba cancelación.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.AreEqual(1, governor.Active);
    }

    [TestMethod]
    public async Task AsyncPauseGateReleasesHundredsOfWaitersWithoutBlockingThreads()
    {
        await using var job = new CopyJob(Array.Empty<DestinationProgress>());
        job.SetPaused(true);
        Assert.IsTrue(job.IsPaused);

        var waits = Enumerable.Range(0, 512)
            .Select(_ => job.WaitIfPausedAsync(CancellationToken.None).AsTask())
            .ToArray();
        Assert.AreEqual(0, waits.Count(task => task.IsCompleted));

        job.SetPaused(false);
        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(job.IsPaused);
    }

    [TestMethod]
    public async Task PipelineGovernorRepeatedCancellationStressDoesNotLeakSlots()
    {
        var governor = new CopyEngine.PipelineGovernor();
        for (var iteration = 0; iteration < 200; iteration++)
        {
            await governor.AcquirePrefetchSlotAsync(CancellationToken.None);
            await governor.AcquirePrefetchSlotAsync(CancellationToken.None);

            using var cancel = new CancellationTokenSource();
            var blocked = Enumerable.Range(0, 16)
                .Select(_ => governor.AcquirePrefetchSlotAsync(cancel.Token).AsTask())
                .ToArray();
            cancel.Cancel();
            foreach (var task in blocked)
            {
                try
                {
                    await task;
                    Assert.Fail("Se esperaba cancelación.");
                }
                catch (OperationCanceledException)
                {
                }
            }

            governor.ReleasePrefetchSlot();
            governor.ReleasePrefetchSlot();
            Assert.AreEqual(0, governor.InFlight);
        }
    }

    [TestMethod]
    public async Task FanOutStressWithRapidPauseResumePreservesEveryDestination()
    {
        using var temp = new TempDirectory("fanout-pause-stress");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[24 * 1024 * 1024 + 193];
        new Random(314159).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "stress.bin"), payload);

        var destinations = Enumerable.Range(0, 8)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);

        for (var cycle = 0; cycle < 20 && !job.Completion.IsCompleted; cycle++)
        {
            job.SetPaused(true);
            await Task.Delay(5);
            job.SetPaused(false);
            await Task.Delay(5);
        }
        job.SetPaused(false);

        await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));
        AssertHealthy(job);
        foreach (var destination in destinations)
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "stress.bin")));
    }

    [TestMethod]
    public void ParallelBlake3IncrementalMatchesSerialExactly()
    {
        var payload = new byte[17 * 1024 * 1024 + 997];
        new Random(271828).NextBytes(payload);
        using var serial = Hasher.New();
        using var parallel = Hasher.New();
        const int step = 4 * 1024 * 1024;
        for (var offset = 0; offset < payload.Length; offset += step)
        {
            var length = Math.Min(step, payload.Length - offset);
            var span = payload.AsSpan(offset, length);
            serial.Update(span);
            parallel.UpdateWithJoin(span);
        }
        CollectionAssert.AreEqual(
            serial.Finalize().AsSpan().ToArray(),
            parallel.Finalize().AsSpan().ToArray());
    }

    [TestMethod]
    public async Task DiagnosticsSnapshotMeasuresCopyAndVerificationHotPaths()
    {
        using var temp = new TempDirectory("telemetry-hotpaths");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[20 * 1024 * 1024 + 113];
        new Random(112358).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var destinations = Enumerable.Range(0, 2)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();

        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        AssertHealthy(job);

        var metrics = job.DiagnosticsSnapshot();
        Assert.IsTrue(metrics.SourceReadBytes >= payload.Length);
        Assert.IsTrue(metrics.SourceHashBytes >= payload.Length);
        Assert.IsTrue(metrics.WrittenBytes >= (long)payload.Length * destinations.Length);
        Assert.IsTrue(metrics.VerifyReadBytes >= (long)payload.Length * destinations.Length);
        Assert.IsTrue(metrics.VerifyHashBytes >= (long)payload.Length * destinations.Length);
        Assert.IsTrue(metrics.PeakBufferedBytes > 0);
        Assert.IsTrue(metrics.MaximumObservedBufferTargetBytes >= metrics.PeakBufferedBytes);
        Assert.AreEqual(destinations.Length, metrics.DurableFlushes);
        Assert.AreEqual(destinations.Length, metrics.Commits);
        Assert.AreEqual(destinations.Length, metrics.RecoveryEvents);
    }

    [TestMethod]
    public async Task AdaptivePipelinePrefetchPreservesExactLargeFileFanOut()
    {
        using var temp = new TempDirectory("adaptive-pipeline-prefetch");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[48 * 1024 * 1024 + 731];
        new Random(86420).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), payload);

        var destinations = Enumerable.Range(0, 4)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        AssertHealthy(job);

        foreach (var destination in destinations)
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "large.bin")));
    }

    [TestMethod]
    public async Task FeedbackGovernedVerificationPreservesIntegrity()
    {
        using var temp = new TempDirectory("feedback-governor-integrity");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        for (var index = 0; index < 6; index++)
        {
            var payload = new byte[1024 * 1024 + index * 4093 + 17];
            new Random(24680 + index).NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(source, $"payload-{index}.bin"), payload);
        }

        var destinations = Enumerable.Range(0, Math.Max(3, Math.Min(6, Environment.ProcessorCount)))
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);
    }

    [TestMethod]
    public async Task FanOutTenDestinationsRemainExactWithoutWorkerStarvation()
    {
        using var temp = new TempDirectory("fanout-ten-destinations");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[2 * 1024 * 1024 + 113];
        new Random(13579).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var destinations = Enumerable.Range(0, 10)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();

        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);

        foreach (var destination in destinations)
            CollectionAssert.AreEqual(
                payload,
                await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "payload.bin")));
    }

    [TestMethod]
    public async Task FanOutVerifiesEightDestinationsConcurrently()
    {
        using var temp = new TempDirectory("fanout-verify-eight");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[1024 * 1024 + 31];
        new Random(24680).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), payload);
        var destinations = Enumerable.Range(0, 8)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();

        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        AssertHealthy(job);

        foreach (var destination in destinations)
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "payload.bin")));
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
    public async Task FanOutMixedBufferSizesRemainExactAcrossFiveDestinations()
    {
        using var temp = new TempDirectory("fanout-mixed-byte-budget");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var files = new Dictionary<string, byte[]>
        {
            ["tiny.bin"] = new byte[31 * 1024 + 7],
            ["medium.bin"] = new byte[900 * 1024 + 13],
            ["large.bin"] = new byte[3 * 1024 * 1024 + 29],
            ["prefetch.bin"] = new byte[20 * 1024 * 1024 + 41],
        };
        var seed = 7000;
        foreach (var (name, payload) in files)
        {
            new Random(seed++).NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(source, name), payload);
        }

        var destinations = Enumerable.Range(0, 5)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);

        foreach (var destination in destinations)
        {
            var root = Path.Combine(destination, "Origen");
            foreach (var (name, payload) in files)
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(root, name)));
        }
    }

    [TestMethod]
    public async Task FanOutPrefetchPreservesOrderingAcrossThreeLargeDestinations()
    {
        using var temp = new TempDirectory("fanout-prefetch-order");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[40 * 1024 * 1024 + 777];
        new Random(54321).NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, "prefetch.bin"), payload);
        var destinations = Enumerable.Range(0, 3)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();

        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);

        foreach (var destination in destinations)
            CollectionAssert.AreEqual(
                payload,
                await File.ReadAllBytesAsync(Path.Combine(destination, "Origen", "prefetch.bin")));
    }

    [TestMethod]
    public async Task FanOutHandlesDenseSmallFileTreeAcrossFourDestinations()
    {
        using var temp = new TempDirectory("fanout-small-files");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 256; index++)
        {
            var relative = Path.Combine($"d{index % 8}", $"f{index:D4}.bin");
            var path = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new byte[1 + (index * 997) % (48 * 1024)];
            new Random(10_000 + index).NextBytes(payload);
            await File.WriteAllBytesAsync(path, payload);
            expected[relative] = payload;
        }

        var destinations = Enumerable.Range(0, 4)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(45));
        AssertHealthy(job);

        foreach (var destination in destinations)
        {
            var root = Path.Combine(destination, "Origen");
            foreach (var (relative, payload) in expected)
                CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(root, relative)));
        }
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

    private static RecoveryFile RecoveryFileFor(string sourcePath, string relativePath)
    {
        var info = new FileInfo(sourcePath);
        var ns = checked((info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100L);
        return new RecoveryFile(sourcePath, relativePath, info.Length, ns);
    }

    private static void WriteLegacyRecoveryProof(string destination, RecoveryFile file, byte[] hash)
    {
        StateLayout.PrepareStateDirectory(destination);
        File.WriteAllText(
            StateLayout.JournalPath(destination),
            $"{{\"key\":\"{RecoveryManager.StateKey(file)}\"}}\n");
        File.WriteAllText(
            StateLayout.ManifestPath(destination),
            $"{Convert.ToHexString(hash).ToLowerInvariant()}  {RecoveryManager.ManifestKey(file.RelativePath)}\n");
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

