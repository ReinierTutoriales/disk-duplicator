from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / "dotnet/RepartoCopier.Core/CopyEngine.cs"
MODELS = ROOT / "dotnet/RepartoCopier.Core/Models.cs"
UI = ROOT / "dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs"
TEST = ROOT / "dotnet/RepartoCopier.Core.Tests/ProductionFastPathTests.cs"


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected 1 match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"{label}: expected 1 match, found {count}")
    return updated

# Production defaults: normal copies do not perform a full physical read-back.
models = MODELS.read_text(encoding="utf-8")
models = replace_once(
    models,
    "public sealed record CopyOptions(\n    bool Verify = true,\n    bool SkipSame = true,\n    bool KeepGoing = false);",
    "public sealed record CopyOptions(\n    bool Verify = false,\n    bool SkipSame = true,\n    bool KeepGoing = false);",
    "CopyOptions production verify default",
)
MODELS.write_text(models, encoding="utf-8")

ui = UI.read_text(encoding="utf-8")
ui = replace_once(ui, "                Verify: true,", "                Verify: false,", "WinUI verify option")
ui = replace_once(ui, '                        : "Copia completada y verificada";', '                        : "Copia completada";', "completion status")
UI.write_text(ui, encoding="utf-8")

engine = ENGINE.read_text(encoding="utf-8")
engine = engine.replace("using System.Collections.Concurrent;\n", "", 1)
engine = replace_once(
    engine,
    "    private const int SourcePrefetchPhysicalCapacity = 4;\n    private const int SourcePrefetchThreshold = 16 * 1024 * 1024;",
    "    private const int SourcePrefetchPhysicalCapacity = 4;\n    private const int SourceHashPipelineCapacity = 2;\n    private const int SourcePrefetchThreshold = 16 * 1024 * 1024;",
    "source hash pipeline constant",
)
engine = engine.replace("            Verify: true,\n            SkipSame: plan.SkipSame,", "            Verify: false,\n            SkipSame: plan.SkipSame,", 2)
if engine.count("Verify: false,\n            SkipSame: plan.SkipSame,") < 2:
    raise SystemExit("CopyEngine production defaults were not updated twice")

# expectedHashes is producer-owned. Writers do not need to rewrite the same hash N times.
engine = replace_once(
    engine,
    "        var expectedHashes = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);",
    "        var expectedHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);",
    "expected hash dictionary",
)
engine = replace_once(
    engine,
    "                .Select(worker => WriterLoopAsync(worker, options, job, expectedHashes))",
    "                .Select(worker => WriterLoopAsync(worker, options, job))",
    "writer task args",
)
engine = replace_once(
    engine,
    "        bool[][] skipMasks,\n        ConcurrentDictionary<string, byte[]> expectedHashes,\n        CopyJob job,",
    "        bool[][] skipMasks,\n        Dictionary<string, byte[]> expectedHashes,\n        CopyJob job,",
    "producer hash dictionary type",
)
engine = replace_once(
    engine,
    "        DestinationWorker worker,\n        CopyOptions options,\n        CopyJob job,\n        ConcurrentDictionary<string, byte[]> expectedHashes)",
    "        DestinationWorker worker,\n        CopyOptions options,\n        CopyJob job)",
    "writer signature",
)
engine = replace_once(
    engine,
    "                            FinishFile(worker, current, end.Hash, options, recovery, job);\n                            if (!current.Failed)\n                                expectedHashes[PathKey(current.Entry.RelativePath)] = end.Hash;\n                            current = null;",
    "                            FinishFile(worker, current, end.Hash, options, recovery, job);\n                            current = null;",
    "redundant writer hash update",
)
engine = replace_once(
    engine,
    "        ConcurrentDictionary<string, byte[]> expectedHashes,\n        CopyJob job,\n        ResourceGovernor resources)",
    "        IReadOnlyDictionary<string, byte[]> expectedHashes,\n        CopyJob job,\n        ResourceGovernor resources)",
    "verify hash dictionary type",
)

# Remove hot-path recipient-array allocation: SharedBlock refs are assigned to the active list;
# DeliverOneAsync already releases the reference if a worker dies before admission.
engine = replace_once(
    engine,
    "            var recipients = active.Where(worker => worker.IsActive).ToArray();\n            if (recipients.Length == 0)\n            {\n                ArrayPool<byte>.Shared.Return(rented);\n                bufferBudget.Release(readBufferSize);\n                return null;\n            }\n\n            var block = new SharedBlock(rented, read, readBufferSize, recipients.Length, bufferBudget);\n            var deliveryStarted = Stopwatch.GetTimestamp();\n            await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);",
    "            active.RemoveAll(worker => !worker.IsActive);\n            if (active.Count == 0)\n            {\n                ArrayPool<byte>.Shared.Return(rented);\n                bufferBudget.Release(readBufferSize);\n                return null;\n            }\n\n            var block = new SharedBlock(rented, read, readBufferSize, active.Count, bufferBudget);\n            var deliveryStarted = Stopwatch.GetTimestamp();\n            await DeliverAsync(active, new DataMessage(block), countsData: true, job).ConfigureAwait(false);",
    "sequential recipients allocation",
)
engine = replace_once(
    engine,
    "                var recipients = active.Where(worker => worker.IsActive).ToArray();\n                if (recipients.Length == 0)\n                {\n                    sourceBlock.Release();\n                    stoppedEarly = true;\n                    prefetchCancel.Cancel();\n                    break;\n                }\n\n                var shared = sourceBlock.TransferToShared(recipients.Length);\n                var deliveryStarted = Stopwatch.GetTimestamp();\n                await DeliverAsync(recipients, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);",
    "                active.RemoveAll(worker => !worker.IsActive);\n                if (active.Count == 0)\n                {\n                    sourceBlock.Release();\n                    stoppedEarly = true;\n                    prefetchCancel.Cancel();\n                    break;\n                }\n\n                var shared = sourceBlock.TransferToShared(active.Count);\n                var deliveryStarted = Stopwatch.GetTimestamp();\n                await DeliverAsync(active, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);",
    "prefetched recipients allocation",
)
engine = replace_once(
    engine,
    "                await DeliverAsync(active.Where(worker => worker.IsActive).ToArray(), new EndMessage(hash), countsData: false, job).ConfigureAwait(false);",
    "                active.RemoveAll(worker => !worker.IsActive);\n                await DeliverAsync(active, new EndMessage(hash), countsData: false, job).ConfigureAwait(false);",
    "end recipients allocation",
)

# Two-stage source pipeline. Reader can fetch N+1 while the single ordered hash stage hashes N.
# The existing PipelineGovernor + AdaptiveByteBudget still bound all live buffers.
new_prefetch = r'''    private static async Task<SourceReadResult> PrefetchSourceAsync(
        FileEntry entry,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        CancellationToken token)
    {
        Exception? completionError = null;
        using var stageCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var hashQueue = Channel.CreateBounded<SourceReadBlock>(new BoundedChannelOptions(SourceHashPipelineCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var readTask = ReadSourceAheadAsync(
            entry,
            readBufferSize,
            hashQueue.Writer,
            bufferBudget,
            job,
            pipeline,
            stageCancel.Token);

        try
        {
            using var hasher = Hasher.New();
            long totalHashed = 0;
            await foreach (var block in hashQueue.Reader.ReadAllAsync(stageCancel.Token).ConfigureAwait(false))
            {
                totalHashed += block.Length;
                var hashStarted = Stopwatch.GetTimestamp();
                hasher.UpdateWithJoin(block.Memory.Span);
                job.Telemetry.RecordSourceHash(block.Length, Stopwatch.GetElapsedTime(hashStarted));
                try
                {
                    await output.WriteAsync(block, stageCancel.Token).ConfigureAwait(false);
                }
                catch
                {
                    block.Release();
                    pipeline.ReleasePrefetchSlot();
                    throw;
                }
            }

            var totalRead = await readTask.ConfigureAwait(false);
            if (totalHashed != totalRead)
                throw new IOException($"La tubería de origen perdió datos en {entry.RelativePath}: leídos {totalRead}, procesados {totalHashed}.");
            ValidateCompletedSourceRead(entry, totalRead);
            return new SourceReadResult(totalRead, hasher.Finalize().AsSpan().ToArray());
        }
        catch (Exception ex)
        {
            completionError = ex;
            stageCancel.Cancel();
            try { await readTask.ConfigureAwait(false); }
            catch { }
            while (hashQueue.Reader.TryRead(out var leftover))
            {
                leftover.Release();
                pipeline.ReleasePrefetchSlot();
            }
            throw;
        }
        finally
        {
            output.TryComplete(completionError);
        }
    }

    private static async Task<long> ReadSourceAheadAsync(
        FileEntry entry,
        int readBufferSize,
        ChannelWriter<SourceReadBlock> output,
        AdaptiveByteBudget bufferBudget,
        CopyJob job,
        PipelineGovernor pipeline,
        CancellationToken token)
    {
        Exception? completionError = null;
        try
        {
            await using var source = OpenSourceStream(entry.SourcePath);
            long totalRead = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(token).ConfigureAwait(false);
                await pipeline.AcquirePrefetchSlotAsync(token).ConfigureAwait(false);
                var slotOwned = true;
                var budgetOwned = false;
                byte[]? rented = null;
                try
                {
                    var budgetStarted = Stopwatch.GetTimestamp();
                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);
                    budgetOwned = true;
                    var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
                    pipeline.RecordBudgetWait(budgetElapsed);
                    job.Telemetry.RecordBufferWait(budgetElapsed);
                    job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);

                    rented = ArrayPool<byte>.Shared.Rent(readBufferSize);
                    var readStarted = Stopwatch.GetTimestamp();
                    var read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);
                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                    pipeline.RecordSourceRead(readElapsed);
                    job.Telemetry.RecordSourceRead(read, readElapsed);

                    if (read == 0)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                        rented = null;
                        bufferBudget.Release(readBufferSize);
                        budgetOwned = false;
                        pipeline.ReleasePrefetchSlot();
                        slotOwned = false;
                        break;
                    }

                    totalRead += read;
                    var block = new SourceReadBlock(rented, read, readBufferSize, bufferBudget);
                    rented = null;
                    budgetOwned = false; // ownership transferred to SourceReadBlock
                    await output.WriteAsync(block, token).ConfigureAwait(false);
                    slotOwned = false; // released by the downstream sourceQueue consumer
                }
                catch
                {
                    if (rented is not null)
                        ArrayPool<byte>.Shared.Return(rented);
                    if (budgetOwned)
                        bufferBudget.Release(readBufferSize);
                    if (slotOwned)
                        pipeline.ReleasePrefetchSlot();
                    throw;
                }
            }
            return totalRead;
        }
        catch (Exception ex)
        {
            completionError = ex;
            throw;
        }
        finally
        {
            output.TryComplete(completionError);
        }
    }

'''
engine = regex_once(
    engine,
    r"    private static async Task<SourceReadResult> PrefetchSourceAsync\(.*?(?=    private static FileStream OpenSourceStream)",
    new_prefetch,
    "prefetch source pipeline",
)

engine = replace_once(
    engine,
    "        public int Length { get; }\n\n        public SharedBlock TransferToShared(int references)",
    "        public int Length { get; }\n        public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(SourceReadBlock))).AsMemory(0, Length);\n\n        public SharedBlock TransferToShared(int references)",
    "SourceReadBlock memory",
)
engine = engine.replace(
    "            // SharedBlock instances can outlive the producer while destination writers\n            // drain their bounded channels.",
    "            // SharedBlock instances can outlive the producer while destination writers\n            // drain their channels.",
    1,
)
ENGINE.write_text(engine, encoding="utf-8")

TEST.write_text(r'''using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class ProductionFastPathTests
{
    [TestMethod]
    public async Task DefaultProductionCopySkipsPhysicalReadBackAndPreservesBytes()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var payload = new byte[20 * 1024 * 1024 + 733];
        new Random(20260912).NextBytes(payload);
        var sourceFile = Path.Combine(source, "payload.bin");
        await File.WriteAllBytesAsync(sourceFile, payload);

        var destinations = Enumerable.Range(0, 2)
            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-{index}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);

        await using var job = CopyEngine.Start(plan);
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));
        var metrics = job.DiagnosticsSnapshot();
        Assert.AreEqual(0L, metrics.VerifyReadBytes);
        Assert.AreEqual(0L, metrics.VerifyHashBytes);
        Assert.AreEqual(TimeSpan.Zero, metrics.VerifyPhaseElapsed);

        var expected = SHA256.HashData(payload);
        foreach (var destination in destinations)
        {
            var copied = Path.Combine(destination, "Origen", "payload.bin");
            Assert.IsTrue(File.Exists(copied));
            Assert.AreEqual(payload.LongLength, new FileInfo(copied).Length);
            await using var stream = File.OpenRead(copied);
            CollectionAssert.AreEqual(expected, await SHA256.HashDataAsync(stream));
        }
    }

    [TestMethod]
    public async Task ExplicitDiagnosticVerificationRemainsAvailable()
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(source, "verify.bin"), new byte[6 * 1024 * 1024 + 17]);
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var plan = CopyPlan.Create(source, [destination], skipSame: false, keepGoing: false);

        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));
        Assert.IsTrue(job.DiagnosticsSnapshot().VerifyReadBytes > 0);
        Assert.IsTrue(job.DiagnosticsSnapshot().VerifyHashBytes > 0);
        Assert.IsTrue(job.DiagnosticsSnapshot().VerifyPhaseElapsed > TimeSpan.Zero);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repartocopier-fast-{Guid.NewGuid():N}");
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
''', encoding="utf-8")

print("production fast-path optimization staged")
