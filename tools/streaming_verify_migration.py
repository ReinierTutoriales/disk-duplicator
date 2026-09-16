from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
ENGINE = ROOT / 'dotnet/RepartoCopier.Core/CopyEngine.cs'
FAST_TEST = ROOT / 'dotnet/RepartoCopier.Core.Tests/ProductionFastPathTests.cs'
ARCH_TEST = ROOT / 'dotnet/RepartoCopier.Core.Tests/VerificationStreamingArchitectureTests.cs'


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 match, found {count}')
    return text.replace(old, new, 1)

engine = ENGINE.read_text(encoding='utf-8')

# Fixed total verification workspace, divided between source + active destinations.
engine = replace_once(
    engine,
    '    private const long SharedFanoutPoolBytes = 64L * 1024 * 1024;\n',
    '    private const long SharedFanoutPoolBytes = 64L * 1024 * 1024;\n    private const int VerificationWorkspaceBytes = 8 * 1024 * 1024;\n',
    'verification workspace constant')

# Verify gets the source scheduler only when source physically shares a destination device.
engine = replace_once(
    engine,
    '                    await VerifyDestinationsAsync(copy, workers, progress, job).ConfigureAwait(false);',
    '                    await VerifyDestinationsAsync(\n                        copy, workers, progress, job, deviceSchedulers.SharedSourceScheduler).ConfigureAwait(false);',
    'verify call')

# COPY no longer accumulates one CRC descriptor per block.
old = '''                            current.VerificationBlocks.Add(
                                new VerificationBlock(chunkData.Block.Length, chunkData.Block.VerificationCrc32C));
'''
engine = replace_once(engine, old, '', 'remove per-block verification accumulation')

# Completion tracks only which files this branch actually committed. O(files), not O(blocks).
old = '''        worker.VerificationPlans[PathKey(current.Entry.RelativePath)] =
            new VerificationPlan(current.Entry.Size, current.VerificationBlocks.ToArray());
        worker.Progress.MarkDone();
'''
new = '''        worker.CompletedFiles.Add(PathKey(current.Entry.RelativePath));
        worker.Progress.MarkDone();
'''
engine = replace_once(engine, old, new, 'replace verification plans with completed file set')

# Replace the complete verification implementation with source+dest streaming comparison.
start = engine.index('    private static async Task VerifyDestinationsAsync(')
end = engine.index('    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(', start)
streaming_verify = r'''    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        CopyJob job,
        DeviceScheduler? sharedSourceScheduler)
    {
        for (var slot = 0; slot < workers.Length; slot++)
        {
            if (!workers[slot].IsActive)
                continue;
            var entries = copy.Files
                .Where(entry => workers[slot].CompletedFiles.Contains(PathKey(entry.RelativePath)))
                .ToArray();
            var bytes = entries.Aggregate<FileEntry, ulong>(0, (sum, entry) => checked(sum + (ulong)entry.Size));
            progress[slot].SetVerifyWork(bytes, (ulong)entries.Length);
            if (entries.Length > 0)
                progress[slot].SetPhase(DestinationPhase.Verifying);
        }

        foreach (var entry in copy.Files)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            var slots = Enumerable.Range(0, workers.Length)
                .Where(slot => workers[slot].IsActive && workers[slot].CompletedFiles.Contains(PathKey(entry.RelativePath)))
                .ToArray();
            if (slots.Length == 0)
                continue;

            ValidateSourceSnapshot(entry);
            var targets = new List<CoordinatedVerifyTarget>(slots.Length + 1);
            try
            {
                targets.Add(new CoordinatedVerifyTarget(
                    slot: -1,
                    path: entry.SourcePath,
                    device: copy.SourceDevice,
                    scheduler: sharedSourceScheduler,
                    progress: null));

                foreach (var slot in slots)
                {
                    progress[slot].SetLastFile(entry.RelativePath);
                    var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                    if (!File.Exists(destination))
                    {
                        workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                        continue;
                    }
                    ValidateRuntimeDestinationPath(workers[slot].Root, entry.RelativePath);
                    WindowsPath.EnsureRegularFile(destination, "El archivo durante verificación");
                    if (new FileInfo(destination).Length != entry.Size)
                    {
                        workers[slot].Fail($"Tamaño no coincide durante verificación: {destination}");
                        continue;
                    }
                    targets.Add(new CoordinatedVerifyTarget(
                        slot,
                        destination,
                        copy.DestinationDevices[slot],
                        workers[slot].DeviceScheduler,
                        progress[slot]));
                }

                if (targets.Count <= 1)
                    continue;

                var perStreamBytes = SelectVerificationChunkBytes(targets);
                foreach (var target in targets)
                    target.Open(perStreamBytes);

                long offset = 0;
                while (offset < entry.Size)
                {
                    job.Token.ThrowIfCancellationRequested();
                    await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

                    var activeTargets = targets
                        .Where(target => target.IsSource || workers[target.Slot].IsActive)
                        .ToArray();
                    if (activeTargets.Length <= 1)
                        break;

                    var expectedBytes = (int)Math.Min(perStreamBytes, entry.Size - offset);
                    var reads = activeTargets
                        .Select(target => ReadVerifyTargetAsync(target, expectedBytes, offset, job))
                        .ToArray();
                    var results = await Task.WhenAll(reads).ConfigureAwait(false);

                    var sourceIndex = Array.FindIndex(activeTargets, target => target.IsSource);
                    if (sourceIndex < 0 || results[sourceIndex] < expectedBytes)
                        throw new IOException($"Lectura incompleta del origen durante verificación: {entry.SourcePath}");

                    var crcStarted = Stopwatch.GetTimestamp();
                    var sourceCrc = FastCrc32C.Compute(activeTargets[sourceIndex].Buffer!.Memory.Span[..expectedBytes]);
                    job.Telemetry.RecordVerifyCrc32C(expectedBytes, Stopwatch.GetElapsedTime(crcStarted));

                    for (var index = 0; index < activeTargets.Length; index++)
                    {
                        var target = activeTargets[index];
                        if (target.IsSource)
                            continue;
                        if (results[index] < expectedBytes)
                        {
                            workers[target.Slot].Fail($"Lectura incompleta durante verificación: {target.Path}");
                            continue;
                        }

                        crcStarted = Stopwatch.GetTimestamp();
                        var destinationCrc = FastCrc32C.Compute(target.Buffer!.Memory.Span[..expectedBytes]);
                        job.Telemetry.RecordVerifyCrc32C(expectedBytes, Stopwatch.GetElapsedTime(crcStarted));
                        if (destinationCrc != sourceCrc)
                        {
                            workers[target.Slot].Fail($"CRC32C no coincide durante verificación: {target.Path}");
                            continue;
                        }
                        target.Progress!.AddVerified(expectedBytes);
                    }

                    offset = checked(offset + expectedBytes);
                }

                ValidateSourceSnapshot(entry);
                if (offset != entry.Size)
                {
                    foreach (var target in targets.Where(target => !target.IsSource && workers[target.Slot].IsActive))
                        workers[target.Slot].Fail($"Verificación incompleta: {target.Path}");
                }
                else
                {
                    foreach (var target in targets.Where(target => !target.IsSource && workers[target.Slot].IsActive))
                        target.Progress!.MarkVerifyFileDone();
                }
            }
            finally
            {
                foreach (var target in targets)
                    target.Dispose();
            }
        }
    }

    private static int SelectVerificationChunkBytes(IReadOnlyList<CoordinatedVerifyTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(targets));

        var alignment = targets
            .Select(target => Math.Max(1, DirectIoSourceReader.RequiredAlignment(target.Device)))
            .Max();
        var raw = Math.Max(alignment, VerificationWorkspaceBytes / targets.Count);
        var aligned = raw - (raw % alignment);
        return Math.Max(alignment, aligned);
    }

    private static async Task<int> ReadVerifyTargetAsync(
        CoordinatedVerifyTarget target,
        int expectedBytes,
        long offset,
        CopyJob job)
    {
        var requestBytes = target.Direct is null
            ? expectedBytes
            : AlignUp(expectedBytes, target.Direct.Alignment);

        IDisposable? io = null;
        if (target.Scheduler is not null)
            io = await target.Scheduler.AcquireIoAsync(requestBytes, job.Token).ConfigureAwait(false);
        using (io)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                int read;
                if (target.Direct is not null)
                {
                    try
                    {
                        read = await target.Direct.ReadAsync(
                            target.Buffer!, requestBytes, offset, job.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                    {
                        target.SwitchToBuffered();
                        return await ReadVerifyTargetAsync(target, expectedBytes, offset, job).ConfigureAwait(false);
                    }
                }
                else
                {
                    read = await RandomAccess.ReadAsync(
                        target.BufferedHandle!,
                        target.Buffer!.Memory[..expectedBytes],
                        offset,
                        job.Token).ConfigureAwait(false);
                }
                job.Telemetry.RecordVerifyRead(expectedBytes, Stopwatch.GetElapsedTime(started));
                return read;
            }
            catch (Exception ex) when (
                target.Scheduler is not null &&
                TransientIoErrorClassifier.IsTransient(ex) &&
                target.Scheduler.RecordTransientFailure())
            {
                target.Progress?.AddRetry();
                job.Telemetry.RecordIoRecovery(
                    "verify-read",
                    target.Path,
                    target.Direct is null ? "buffered" : "direct",
                    TransientIoErrorClassifier.GetNativeCodeOrZero(ex),
                    target.Scheduler.CurrentQueueDepth,
                    retryCount: 1,
                    offset,
                    recovered: false);
                return await ReadVerifyTargetAsync(target, expectedBytes, offset, job).ConfigureAwait(false);
            }
        }
    }

    private static int AlignUp(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private sealed class CoordinatedVerifyTarget : IDisposable
    {
        internal CoordinatedVerifyTarget(
            int slot,
            string path,
            StorageDeviceInfo device,
            DeviceScheduler? scheduler,
            DestinationProgress? progress)
        {
            Slot = slot;
            Path = path;
            Device = device;
            Scheduler = scheduler;
            Progress = progress;
        }

        internal bool IsSource => Slot < 0;
        internal int Slot { get; }
        internal string Path { get; }
        internal StorageDeviceInfo Device { get; }
        internal DeviceScheduler? Scheduler { get; }
        internal DestinationProgress? Progress { get; }
        internal DirectIoSourceReader.OverlappedSession? Direct { get; private set; }
        internal Microsoft.Win32.SafeHandles.SafeFileHandle? BufferedHandle { get; private set; }
        internal SourceBufferLease? Buffer { get; private set; }

        internal void Open(int maximumBlockBytes)
        {
            var alignment = Math.Max(1, DirectIoSourceReader.RequiredAlignment(Device));
            var directCapacity = AlignUp(Math.Max(1, maximumBlockBytes), Math.Max(1, alignment));
            if (DirectIoSourceReader.TryOpenOverlappedForVerification(Path, Device, directCapacity, out var direct))
            {
                Direct = direct;
                Buffer = SourceBufferLease.RentAligned(
                    directCapacity,
                    Math.Max(Environment.SystemPageSize, alignment));
                return;
            }
            BufferedHandle = File.OpenHandle(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Buffer = SourceBufferLease.RentBuffered(Math.Max(1, maximumBlockBytes));
        }

        internal void SwitchToBuffered()
        {
            Direct?.Dispose();
            Direct = null;
            BufferedHandle ??= File.OpenHandle(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public void Dispose()
        {
            Direct?.Dispose();
            BufferedHandle?.Dispose();
            Buffer?.Dispose();
        }
    }

'''
engine = engine[:start] + streaming_verify + engine[end:]

# Worker tracks only committed file names for optional verify.
engine = replace_once(
    engine,
    '        public Dictionary<string, VerificationPlan> VerificationPlans { get; } = new(StringComparer.Ordinal);\n',
    '        public HashSet<string> CompletedFiles { get; } = new(StringComparer.Ordinal);\n',
    'worker completed files')

# CurrentFile no longer owns verification history.
engine = replace_once(
    engine,
    '        public List<VerificationBlock> VerificationBlocks { get; } = [];\n',
    '',
    'remove current file verification blocks')

# SharedBlock no longer computes or stores CRC during copy.
engine = replace_once(
    engine,
    '            VerificationCrc32C = FastCrc32C.Compute(buffer.Memory.Span[..length]);\n',
    '',
    'remove copy-time CRC')
engine = replace_once(
    engine,
    '        public uint VerificationCrc32C { get; }\n',
    '',
    'remove shared block CRC property')

# Retired constructor created only for branch-detach/replay era.
engine, removed_ctor = re.subn(
    r'\n        internal SharedBlock\(SourceBufferLease buffer, int length, uint verificationCrc32\)\n        \{.*?\n        \}\n',
    '\n',
    engine,
    count=1,
    flags=re.S)
if removed_ctor != 1:
    raise RuntimeError(f'retired SharedBlock CRC constructor: expected 1 match, found {removed_ctor}')

# Remove retired verification record declarations wherever they remain.
engine = re.sub(r'\n\s*private sealed record VerificationBlock\([^;]+;\n', '\n', engine)
engine = re.sub(r'\n\s*private sealed record VerificationPlan\([^;]+;\n', '\n', engine)

for forbidden in ('VerificationPlan', 'VerificationBlock', 'VerificationPlans', 'VerificationCrc32C'):
    if forbidden in engine:
        raise RuntimeError(f'legacy verification state remains in CopyEngine: {forbidden}')

ENGINE.write_text(engine, encoding='utf-8')

# Update functional test: verify now physically reads source + destination.
test = FAST_TEST.read_text(encoding='utf-8')
test = replace_once(
    test,
    '        Assert.AreEqual((long)payloadSize, metrics.VerifyReadBytes);\n        Assert.AreEqual((long)payloadSize, metrics.VerifyCrc32CBytes);\n',
    '        Assert.AreEqual((long)payloadSize * 2, metrics.VerifyReadBytes);\n        Assert.AreEqual((long)payloadSize * 2, metrics.VerifyCrc32CBytes);\n',
    'verify metrics test')
FAST_TEST.write_text(test, encoding='utf-8')

ARCH_TEST.write_text('''using Microsoft.VisualStudio.TestTools.UnitTesting;\n\nnamespace RepartoCopier.Core.Tests;\n\n[TestClass]\npublic sealed class VerificationStreamingArchitectureTests\n{\n    [TestMethod]\n    public void VerifyUsesFixedWorkspaceAndNoPerBlockHistory()\n    {\n        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        Assert.IsTrue(engine.Contains("VerificationWorkspaceBytes = 8 * 1024 * 1024", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("VerificationPlan", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("VerificationBlock", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("VerificationPlans", StringComparison.Ordinal));\n        Assert.IsFalse(engine.Contains("VerificationCrc32C", StringComparison.Ordinal));\n    }\n\n    [TestMethod]\n    public void VerifyReadsSourceAndDestinationsAtSameOffsetAndComparesImmediately()\n    {\n        var engine = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));\n        Assert.IsTrue(engine.Contains("Task.WhenAll(reads)", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("var sourceCrc = FastCrc32C.Compute", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("var destinationCrc = FastCrc32C.Compute", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("destinationCrc != sourceCrc", StringComparison.Ordinal));\n        Assert.IsTrue(engine.Contains("entry.SourcePath", StringComparison.Ordinal));\n    }\n\n    private static string FindRepositoryRoot()\n    {\n        var current = new DirectoryInfo(AppContext.BaseDirectory);\n        while (current is not null)\n        {\n            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln"))) return current.FullName;\n            current = current.Parent;\n        }\n        throw new AssertFailedException("No se encontró la raíz del repositorio.");\n    }\n}\n''', encoding='utf-8')

print('streaming verification migration applied')
