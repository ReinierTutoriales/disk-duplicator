from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')
s = s.replace('    private const int Retries = 2;\n', '    private const int Retries = 2;\n    private const int MaxVerificationParallelism = 8;\n')

s = s.replace('''                        await FinishFileAsync(worker, current, end.Hash, options, job, recovery).ConfigureAwait(false);
''', '''                        FinishFile(worker, current, end.Hash, options, recovery);
''', 1)

old = '''    private static async Task FinishFileAsync(
        DestinationWorker worker,
        CurrentFile current,
        byte[] expectedHash,
        CopyOptions options,
        CopyJob job,
        RecoveryCheckpointWriter recovery)
'''
new = '''    private static void FinishFile(
        DestinationWorker worker,
        CurrentFile current,
        byte[] expectedHash,
        CopyOptions options,
        RecoveryCheckpointWriter recovery)
'''
if old not in s:
    raise SystemExit('FinishFileAsync signature not found')
s = s.replace(old, new, 1)

old = '''        if (current.Stream is not null)
        {
            await current.Stream.FlushAsync(job.Token).ConfigureAwait(false);
            current.Stream.Flush(flushToDisk: true);
            current.Stream.Dispose();
            current.Stream = null;
        }
'''
new = '''        if (current.Stream is not null)
        {
            // BufferSize=1 disables FileStream buffering; one durable flush is enough.
            current.Stream.Flush(flushToDisk: true);
            current.Stream.Dispose();
            current.Stream = null;
        }
'''
if old not in s:
    raise SystemExit('double flush anchor not found')
s = s.replace(old, new, 1)

start = s.index('    private static async Task VerifyDestinationsAsync(')
end = s.index('    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(', start)
new_verify = '''    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        ConcurrentDictionary<string, byte[]> expectedHashes,
        CopyJob job)
    {
        var activeSlots = Enumerable.Range(0, workers.Length)
            .Where(slot => workers[slot].IsActive)
            .ToArray();
        await Parallel.ForEachAsync(
            activeSlots,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(MaxVerificationParallelism, Math.Max(1, activeSlots.Length)),
                CancellationToken = job.Token,
            },
            async (slot, token) =>
            {
                progress[slot].SetPhase(DestinationPhase.Verifying);
                foreach (var entry in copy.Files)
                {
                    token.ThrowIfCancellationRequested();
                    job.WaitIfPaused(token);
                    if (!expectedHashes.TryGetValue(PathKey(entry.RelativePath), out var expected))
                        continue;
                    var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                    if (!File.Exists(destination))
                    {
                        workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                        break;
                    }
                    var actual = await HashFileAsync(destination, token).ConfigureAwait(false);
                    if (!actual.AsSpan().SequenceEqual(expected))
                    {
                        workers[slot].Fail($"BLAKE3 no coincide: {destination}");
                        break;
                    }
                }
            }).ConfigureAwait(false);
    }

'''
s = s[:start] + new_verify + s[end:]

old = '''            var sourceHash = await HashFileAsync(entry.SourcePath, token).ConfigureAwait(false);
            foreach (var slot in candidates)
            {
                var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                var destinationHash = await HashFileAsync(destination, token).ConfigureAwait(false);
                if (destinationHash.AsSpan().SequenceEqual(sourceHash))
                {
                    masks[fileIndex][slot] = true;
                    progress[slot].SetLastFile(entry.RelativePath);
                }
            }
'''
new = '''            var sourceHash = await HashFileAsync(entry.SourcePath, token).ConfigureAwait(false);
            await Parallel.ForEachAsync(
                candidates,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(MaxVerificationParallelism, candidates.Count),
                    CancellationToken = token,
                },
                async (slot, cancellationToken) =>
                {
                    var destination = Path.Combine(copy.DestinationRoots[slot], entry.RelativePath);
                    var destinationHash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
                    if (destinationHash.AsSpan().SequenceEqual(sourceHash))
                    {
                        masks[fileIndex][slot] = true;
                        progress[slot].SetLastFile(entry.RelativePath);
                    }
                }).ConfigureAwait(false);
'''
if old not in s:
    raise SystemExit('SkipSame sequential hash anchor not found')
s = s.replace(old, new, 1)
engine.write_text(s, encoding='utf-8')

tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
s = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task FanOutDeliversMultipleBlocksToEveryDestination()
'''
if anchor not in s:
    raise SystemExit('test anchor not found')
new_test = r'''    [TestMethod]
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

'''
s = s.replace(anchor, new_test + anchor, 1)
tests.write_text(s, encoding='utf-8')
