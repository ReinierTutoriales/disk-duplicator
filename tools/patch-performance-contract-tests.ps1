$ErrorActionPreference = 'Stop'
$coreParity = 'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs'
$text = [IO.File]::ReadAllText($coreParity)

$old = @'
    [TestMethod]
    public async Task WriteThroughSmallFilePathPreservesExactDataAndVerification()
    {
        using var temp = new TempDirectory("writethrough-small-files");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var small = new byte[4096];
        var medium = new byte[1024 * 1024];
        var boundary = new byte[4 * 1024 * 1024];
        var large = new byte[4 * 1024 * 1024 + 1];
        new Random(161803).NextBytes(small);
        new Random(161804).NextBytes(medium);
        new Random(161805).NextBytes(boundary);
        new Random(161806).NextBytes(large);
        await File.WriteAllBytesAsync(Path.Combine(source, "small.bin"), small);
        await File.WriteAllBytesAsync(Path.Combine(source, "medium.bin"), medium);
        await File.WriteAllBytesAsync(Path.Combine(source, "boundary.bin"), boundary);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), large);
        var destinations = Enumerable.Range(0, 3)
            .Select(i => Directory.CreateDirectory(Path.Combine(temp.Path, $"d{i}")).FullName)
            .ToArray();
        await using var job = CopyEngine.Start(
            CopyPlan.Create(source, destinations, false, false),
            new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));
        AssertHealthy(job);
        foreach (var root in destinations)
        {
            var copied = Path.Combine(root, "Origen");
            CollectionAssert.AreEqual(small, await File.ReadAllBytesAsync(Path.Combine(copied, "small.bin")));
            CollectionAssert.AreEqual(medium, await File.ReadAllBytesAsync(Path.Combine(copied, "medium.bin")));
            CollectionAssert.AreEqual(boundary, await File.ReadAllBytesAsync(Path.Combine(copied, "boundary.bin")));
            CollectionAssert.AreEqual(large, await File.ReadAllBytesAsync(Path.Combine(copied, "large.bin")));
        }
        // Only the >4 MiB file should require an explicit FlushFileBuffers per destination.
        Assert.AreEqual(destinations.Length, job.DiagnosticsSnapshot().DurableFlushes);
    }
'@
$new = @'
    [TestMethod]
    public async Task SmallAndMediumFilesPreserveExactDataWithUnifiedDurabilityPath()
    {
        using var temp = new TempDirectory("unified-small-files");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var small = new byte[4096];
        var medium = new byte[1024 * 1024];
        var boundary = new byte[4 * 1024 * 1024];
        var large = new byte[4 * 1024 * 1024 + 1];
        new Random(161803).NextBytes(small);
        new Random(161804).NextBytes(medium);
        new Random(161805).NextBytes(boundary);
        new Random(161806).NextBytes(large);
        await File.WriteAllBytesAsync(Path.Combine(source, "small.bin"), small);
        await File.WriteAllBytesAsync(Path.Combine(source, "medium.bin"), medium);
        await File.WriteAllBytesAsync(Path.Combine(source, "boundary.bin"), boundary);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), large);
        var destinations = Enumerable.Range(0, 3)
            .Select(i => Directory.CreateDirectory(Path.Combine(temp.Path, $"d{i}")).FullName)
            .ToArray();
        await using var job = CopyEngine.Start(
            CopyPlan.Create(source, destinations, false, false),
            new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));
        AssertHealthy(job);
        foreach (var root in destinations)
        {
            var copied = Path.Combine(root, "Origen");
            CollectionAssert.AreEqual(small, await File.ReadAllBytesAsync(Path.Combine(copied, "small.bin")));
            CollectionAssert.AreEqual(medium, await File.ReadAllBytesAsync(Path.Combine(copied, "medium.bin")));
            CollectionAssert.AreEqual(boundary, await File.ReadAllBytesAsync(Path.Combine(copied, "boundary.bin")));
            CollectionAssert.AreEqual(large, await File.ReadAllBytesAsync(Path.Combine(copied, "large.bin")));
        }
        var diagnostics = job.DiagnosticsSnapshot();
        Assert.IsGreaterThanOrEqualTo(destinations.Length * 4, diagnostics.DurableFlushes);
        Assert.AreEqual(destinations.Length * 4, diagnostics.Commits);
        Assert.AreEqual(destinations.Length * 4, diagnostics.RecoveryEvents);
    }
'@
if (-not $text.Contains($old)) { throw 'Legacy WriteThrough small-file test block not found.' }
$text = $text.Replace($old, $new)
if ($text.Contains('WriteThroughSmallFilePathPreservesExactDataAndVerification')) { throw 'Legacy WriteThrough test remains.' }
[IO.File]::WriteAllText($coreParity, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $coreParity
if (git diff --cached --quiet) { throw 'Performance contract test migration produced no changes.' }
git commit -m 'test(core): migrate obsolete write-through contract'
git push origin HEAD:main
