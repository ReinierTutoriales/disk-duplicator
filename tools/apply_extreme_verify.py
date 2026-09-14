from pathlib import Path
import re


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected one match for {label}, found {count}")
    return text.replace(old, new, 1)


def replace_between(text: str, start: str, end: str, replacement: str, label: str) -> str:
    start_index = text.find(start)
    if start_index < 0:
        raise RuntimeError(f"Start marker not found for {label}")
    if text.find(start, start_index + len(start)) >= 0:
        raise RuntimeError(f"Start marker not unique for {label}")
    end_index = text.find(end, start_index)
    if end_index < 0:
        raise RuntimeError(f"End marker not found for {label}")
    return text[:start_index] + replacement.rstrip() + "\n\n" + text[end_index:]


def regex_once(text: str, pattern: str, replacement: str, label: str, flags=0) -> str:
    new, count = re.subn(pattern, replacement, text, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f"Expected one regex match for {label}, found {count}")
    return new


core = Path("dotnet/RepartoCopier.Core")
tests = Path("dotnet/RepartoCopier.Core.Tests")
ui = Path("dotnet/RepartoCopier.WinUI")

# Fast CRC32, matching the IEEE polynomial used by ExtremeCopy-style validation.
(core / "FastCrc32.cs").write_text(r'''namespace RepartoCopier.Core;

internal static class FastCrc32
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildTable();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
            crc = Table[(byte)(crc ^ value)] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            table[i] = value;
        }
        return table;
    }
}

internal readonly record struct VerificationBlock(int Length, uint Crc32);
internal sealed record VerificationPlan(long Length, IReadOnlyList<VerificationBlock> Blocks);
''', encoding="utf-8", newline="\n")

(core / "FastVerificationReader.cs").write_text(r'''using System.Diagnostics;

namespace RepartoCopier.Core;

internal static class FastVerificationReader
{
    private const int DirectThreshold = 4 * 1024 * 1024;
    private const int DirectProbeSize = 8 * 1024 * 1024;

    internal static async Task<bool> VerifyAsync(
        string path,
        StorageDeviceInfo device,
        DeviceScheduler scheduler,
        VerificationPlan plan,
        CopyJob job,
        DestinationProgress progress)
    {
        if (plan.Blocks.Count == 0)
            return plan.Length == 0;

        if (plan.Length >= DirectThreshold &&
            DirectIoSourceReader.TryOpenOverlappedForVerification(path, device, DirectProbeSize, out var direct))
        {
            using (direct)
            {
                try
                {
                    return await VerifyDirectAsync(path, device, scheduler, plan, direct!, job, progress).ConfigureAwait(false);
                }
                catch (Exception ex) when (DirectIoSourceReader.IsFallbackable(ex))
                {
                    // Unsupported unbuffered/overlapped combinations fall back to the
                    // portable asynchronous reader. Hardware faults remain fatal.
                }
            }
        }

        return await VerifyBufferedAsync(path, plan, scheduler, job, progress).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyDirectAsync(
        string path,
        StorageDeviceInfo device,
        DeviceScheduler scheduler,
        VerificationPlan plan,
        DirectIoSourceReader.OverlappedSession session,
        CopyJob job,
        DestinationProgress progress)
    {
        var depth = device.MediaKind == StorageMediaKind.SolidState
            ? Math.Clamp(scheduler.MaxOutstandingIo, 1, 2)
            : 1;
        var pending = new Queue<PendingRead>();
        long offset = 0;
        var index = 0;

        while (index < plan.Blocks.Count || pending.Count > 0)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);

            while (index < plan.Blocks.Count && pending.Count < depth)
            {
                var expected = plan.Blocks[index++];
                var requestSize = AlignUp(expected.Length, session.Alignment);
                var lease = SourceBufferLease.RentAligned(requestSize, session.Alignment);
                var started = Stopwatch.GetTimestamp();
                var task = ReadDirectAsync(session, scheduler, lease, requestSize, offset, job.Token);
                pending.Enqueue(new PendingRead(offset, expected, lease, started, task));
                offset = checked(offset + expected.Length);
            }

            var current = pending.Dequeue();
            try
            {
                var read = await current.Read.ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(current.Started);
                job.Telemetry.RecordVerifyRead(current.Expected.Length, elapsed);
                if (read < current.Expected.Length)
                    throw new IOException($"Lectura incompleta durante verificación: {path}");

                var crcStarted = Stopwatch.GetTimestamp();
                var actual = FastCrc32.Compute(current.Buffer.Memory.Span[..current.Expected.Length]);
                job.Telemetry.RecordVerifyHash(current.Expected.Length, Stopwatch.GetElapsedTime(crcStarted));
                if (actual != current.Expected.Crc32)
                    return false;
                progress.AddVerified(current.Expected.Length);
            }
            finally
            {
                current.Buffer.Dispose();
            }
        }

        return offset == plan.Length;
    }

    private static async Task<int> ReadDirectAsync(
        DirectIoSourceReader.OverlappedSession session,
        DeviceScheduler scheduler,
        SourceBufferLease buffer,
        int requestSize,
        long offset,
        CancellationToken token)
    {
        using var io = await scheduler.AcquireIoAsync(token).ConfigureAwait(false);
        return await session.ReadAsync(buffer, requestSize, offset, token).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyBufferedAsync(
        string path,
        VerificationPlan plan,
        DeviceScheduler scheduler,
        CopyJob job,
        DestinationProgress progress)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long total = 0;
        foreach (var expected in plan.Blocks)
        {
            job.Token.ThrowIfCancellationRequested();
            await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
            using var buffer = SourceBufferLease.RentBuffered(expected.Length);
            var filled = 0;
            var started = Stopwatch.GetTimestamp();
            using (var io = await scheduler.AcquireIoAsync(job.Token).ConfigureAwait(false))
            {
                while (filled < expected.Length)
                {
                    var read = await stream.ReadAsync(buffer.Memory[filled..expected.Length], job.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    filled += read;
                }
            }
            job.Telemetry.RecordVerifyRead(filled, Stopwatch.GetElapsedTime(started));
            if (filled != expected.Length)
                return false;

            var crcStarted = Stopwatch.GetTimestamp();
            var actual = FastCrc32.Compute(buffer.Memory.Span[..filled]);
            job.Telemetry.RecordVerifyHash(filled, Stopwatch.GetElapsedTime(crcStarted));
            if (actual != expected.Crc32)
                return false;
            progress.AddVerified(filled);
            total = checked(total + filled);
        }
        return total == plan.Length && stream.Position == plan.Length;
    }

    private static int AlignUp(int value, int alignment)
    {
        var remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private sealed record PendingRead(
        long Offset,
        VerificationBlock Expected,
        SourceBufferLease Buffer,
        long Started,
        Task<int> Read);
}
''', encoding="utf-8", newline="\n")

# Extend Direct I/O with a genuine OVERLAPPED verification handle.
direct_path = core / "DirectIoSourceReader.cs"
direct = direct_path.read_text(encoding="utf-8")
direct = replace_once(
    direct,
    "    private const uint FileFlagSequentialScan = 0x08000000;",
    "    private const uint FileFlagSequentialScan = 0x08000000;\n    private const uint FileFlagOverlapped = 0x40000000;",
    "overlapped flag",
)
overlap_open = r'''    internal static bool TryOpenOverlappedForVerification(
        string path,
        StorageDeviceInfo device,
        int transferSize,
        out OverlappedSession? session)
    {
        session = null;
        if (!IsVerificationEligible(device, transferSize))
            return false;

        var handle = NativeMethods.CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagNoBuffering | FileFlagSequentialScan | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        session = new OverlappedSession(handle, RequiredAlignment(device));
        return true;
    }

'''
direct = replace_once(direct, "    internal static bool IsFallbackable(Exception error) =>", overlap_open + "    internal static bool IsFallbackable(Exception error) =>", "overlapped open")
overlap_session = r'''    internal sealed class OverlappedSession : IDisposable
    {
        private SafeFileHandle? _handle;

        internal OverlappedSession(SafeFileHandle handle, int alignment)
        {
            _handle = handle;
            Alignment = alignment;
        }

        internal int Alignment { get; }

        internal async Task<int> ReadAsync(
            SourceBufferLease buffer,
            int bytesToRead,
            long fileOffset,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (bytesToRead <= 0 || bytesToRead % Alignment != 0)
                throw new ArgumentOutOfRangeException(nameof(bytesToRead));
            if (fileOffset < 0 || fileOffset % Alignment != 0)
                throw new ArgumentOutOfRangeException(nameof(fileOffset));
            if (!buffer.IsPinned || buffer.Pointer.ToInt64() % Alignment != 0)
                throw new InvalidOperationException("El buffer OVERLAPPED no está alineado al sector físico.");

            var handle = _handle ?? throw new ObjectDisposedException(nameof(OverlappedSession));
            try
            {
                return await RandomAccess.ReadAsync(handle, buffer.Memory[..bytesToRead], fileOffset, token).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                var code = ex.HResult & 0xFFFF;
                throw new DirectIoReadException(code, ex.Message);
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
    }

'''
direct = replace_once(direct, "    internal sealed class DirectIoReadException : IOException", overlap_session + "    internal sealed class DirectIoReadException : IOException", "overlapped session")
direct_path.write_text(direct, encoding="utf-8", newline="\n")

# Replace the BLAKE3 post-copy verification path with block CRC32 plans.
engine_path = core / "CopyEngine.cs"
engine = engine_path.read_text(encoding="utf-8")
engine = replace_once(
    engine,
    "await VerifyDestinationsAsync(copy, workers, progress, expectedHashes, job, resources).ConfigureAwait(false);",
    "await VerifyDestinationsAsync(copy, workers, progress, job).ConfigureAwait(false);",
    "verify invocation",
)

verify_method = r'''    private static async Task VerifyDestinationsAsync(
        PreparedCopy copy,
        DestinationWorker[] workers,
        DestinationProgress[] progress,
        CopyJob job)
    {
        var activeSlots = Enumerable.Range(0, workers.Length)
            .Where(slot => workers[slot].IsActive)
            .ToArray();
        var tasks = activeSlots.Select(async slot =>
        {
            var verifyEntries = copy.Files
                .Where(entry => workers[slot].VerificationPlans.ContainsKey(PathKey(entry.RelativePath)))
                .ToArray();
            var verifyBytes = verifyEntries.Aggregate<FileEntry, ulong>(
                0,
                (sum, entry) => checked(sum + (ulong)entry.Size));
            progress[slot].SetVerifyWork(verifyBytes, (ulong)verifyEntries.Length);
            if (verifyEntries.Length == 0)
                return;

            progress[slot].SetPhase(DestinationPhase.Verifying);
            foreach (var entry in verifyEntries)
            {
                job.Token.ThrowIfCancellationRequested();
                await job.WaitIfPausedAsync(job.Token).ConfigureAwait(false);
                var key = PathKey(entry.RelativePath);
                if (!workers[slot].VerificationPlans.TryGetValue(key, out var plan))
                    continue;
                progress[slot].SetLastFile(entry.RelativePath);
                var destination = Path.Combine(workers[slot].Root, entry.RelativePath);
                if (!File.Exists(destination))
                {
                    workers[slot].Fail($"Falta el archivo durante verificación: {destination}");
                    break;
                }
                ValidateRuntimeDestinationPath(workers[slot].Root, entry.RelativePath);
                WindowsPath.EnsureRegularFile(destination, "El archivo durante verificación");
                if (new FileInfo(destination).Length != entry.Size)
                {
                    workers[slot].Fail($"Tamaño no coincide durante verificación: {destination}");
                    break;
                }

                var valid = await FastVerificationReader.VerifyAsync(
                    destination,
                    copy.DestinationDevices[slot],
                    workers[slot].DeviceScheduler,
                    plan,
                    job,
                    progress[slot]).ConfigureAwait(false);
                if (!valid)
                {
                    workers[slot].Fail($"CRC32 no coincide durante verificación: {destination}");
                    break;
                }
                progress[slot].MarkVerifyFileDone();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }'''
engine = replace_between(engine, "    private static async Task VerifyDestinationsAsync(", "    private static async Task<bool[][]> BuildVerifiedSkipMasksAsync(", verify_method, "verify method")

# Add one CRC per SharedBlock, computed once before fan-out.
shared_start = engine.find("    internal sealed class SharedBlock")
shared_end = engine.find("    internal sealed class PipelineGovernor", shared_start)
if shared_start < 0 or shared_end < 0:
    raise RuntimeError("SharedBlock section not found")
shared = engine[shared_start:shared_end]
shared = replace_once(shared, "            Length = length;", "            Length = length;\n            VerificationCrc32 = FastCrc32.Compute(buffer.Memory.Span[..length]);", "shared crc assignment")
shared = replace_once(shared, "        public int Length { get; }", "        public int Length { get; }\n        public uint VerificationCrc32 { get; }", "shared crc property")
engine = engine[:shared_start] + shared + engine[shared_end:]

# Destination workers retain source CRC plans only for files they actually wrote.
engine = regex_once(
    engine,
    r"(\n\s*(?:private|internal) sealed class DestinationWorker[^\n]*\n\s*\{)",
    r"\1\n        public Dictionary<string, VerificationPlan> VerificationPlans { get; } = new(StringComparer.Ordinal);",
    "destination verification store",
)
engine = regex_once(
    engine,
    r"(\n\s*(?:private|internal) sealed class CurrentFile[^\n]*\n\s*\{)",
    r"\1\n        public List<VerificationBlock> VerificationBlocks { get; } = [];",
    "current-file verification blocks",
)
engine = replace_once(
    engine,
    "                            await WriteWithRetryAsync(worker, current, data.Block.Memory, job).ConfigureAwait(false);",
    "                            await WriteWithRetryAsync(worker, current, data.Block.Memory, job).ConfigureAwait(false);\n                            current.VerificationBlocks.Add(new VerificationBlock(data.Block.Length, data.Block.VerificationCrc32));",
    "capture verification block",
)
engine = replace_once(
    engine,
    "        worker.Progress.MarkDone();",
    "        worker.VerificationPlans[PathKey(current.Entry.RelativePath)] =\n            new VerificationPlan(current.Entry.Size, current.VerificationBlocks.ToArray());\n        worker.Progress.MarkDone();",
    "persist verification plan",
)
engine_path.write_text(engine, encoding="utf-8", newline="\n")

# Remove the visible verify selector and force verification in the app workflow.
xaml_path = ui / "MainWindow.xaml"
xaml = xaml_path.read_text(encoding="utf-8")
xaml, removed = re.subn(r'\s*<CheckBox x:Name="VerifyCheck"[^\n]*/>\n', '\n', xaml, count=1)
if removed != 1:
    raise RuntimeError(f"Expected to remove one VerifyCheck, removed {removed}")
xaml_path.write_text(xaml, encoding="utf-8", newline="\n")

cs_path = ui / "MainWindow.xaml.cs"
cs = cs_path.read_text(encoding="utf-8")
cs = replace_once(cs, "                Verify: VerifyCheck.IsChecked == true,", "                Verify: true,", "force UI verification")
cs = cs.replace('            OverallDetailText.Text = options.Verify ? "Preparando copia con verificación de integridad..." : "Preparando...";', '            OverallDetailText.Text = "Preparando copia con verificación rápida...";')
cs = re.sub(r'^\s*VerifyCheck\.[^\n]*\n', '', cs, flags=re.MULTILINE)
cs_path.write_text(cs, encoding="utf-8", newline="\n")

# Permanent CRC correctness gate.
(tests / "FastCrc32Tests.cs").write_text(r'''using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class FastCrc32Tests
{
    [TestMethod]
    public void MatchesStandardCrc32CheckVector()
    {
        var bytes = Encoding.ASCII.GetBytes("123456789");
        Assert.AreEqual(0xCBF43926u, FastCrc32.Compute(bytes));
    }

    [TestMethod]
    public void DifferentPayloadsProduceDifferentBlockChecksums()
    {
        var first = new byte[1024 * 1024];
        var second = new byte[first.Length];
        second[^1] = 1;
        Assert.AreNotEqual(FastCrc32.Compute(first), FastCrc32.Compute(second));
    }
}
''', encoding="utf-8", newline="\n")

# Make the production verification test assert the CRC path, not BLAKE3 semantics.
prod_path = tests / "ProductionFastPathTests.cs"
prod = prod_path.read_text(encoding="utf-8")
prod = replace_once(prod, "public async Task ExplicitDiagnosticVerificationRemainsAvailable()", "public async Task ExplicitFastVerificationCoversEveryDestinationByte()", "verification test name")
prod_path.write_text(prod, encoding="utf-8", newline="\n")

print("Extreme-style verification patch applied")
