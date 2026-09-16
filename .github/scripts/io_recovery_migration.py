from pathlib import Path
import re


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one occurrence, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


# Unified native Win32 semantics.
write(
    "dotnet/RepartoCopier.Core/TransientIoErrorClassifier.cs",
    '''namespace RepartoCopier.Core;

/// <summary>
/// Extracts and classifies native Win32 I/O failures without manufacturing
/// HRESULT values. Direct I/O exceptions expose their native code explicitly;
/// generic IOException values are accepted only for FACILITY_WIN32 HRESULTs.
/// </summary>
internal static class TransientIoErrorClassifier
{
    internal static bool TryGetNativeCode(Exception error, out int code)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case DirectIoDestinationWriter.DirectIoWriteException directWrite:
                code = directWrite.NativeErrorCode;
                return code > 0;
            case DirectIoSourceReader.DirectIoReadException directRead:
                code = directRead.NativeErrorCode;
                return code > 0;
            case IOException io:
            {
                var hr = unchecked((uint)io.HResult);
                if ((hr & 0xFFFF0000u) == 0x80070000u)
                {
                    code = (int)(hr & 0xFFFFu);
                    return code > 0;
                }
                break;
            }
        }

        code = 0;
        return false;
    }

    internal static int GetNativeCodeOrZero(Exception error) =>
        TryGetNativeCode(error, out var code) ? code : 0;

    internal static bool IsTransient(Exception error)
    {
        if (!TryGetNativeCode(error, out var code))
            return false;
        return code is
            32 or 33 or 54 or 64 or 121 or
            1231 or 1232 or 1233 or 1236 or 1237;
    }

    internal static bool IsDirectFallbackable(Exception error)
    {
        if (!TryGetNativeCode(error, out var code))
            return false;
        return code is 1 or 5 or 50 or 87;
    }
}
''',
)

for path in (
    "dotnet/RepartoCopier.Core/DirectIoDestinationWriter.cs",
    "dotnet/RepartoCopier.Core/DirectIoSourceReader.cs",
):
    text = read(path)
    text, count = re.subn(
        r"internal static bool IsFallbackable\(Exception error\) =>\s*"
        r"error is DirectIo(?:Write|Read)Exception direct && direct\.NativeErrorCode is\s*"
        r"1 or\s*// ERROR_INVALID_FUNCTION\s*"
        r"5 or\s*// ERROR_ACCESS_DENIED\s*"
        r"50 or\s*// ERROR_NOT_SUPPORTED\s*"
        r"87;\s*// ERROR_INVALID_PARAMETER",
        "internal static bool IsFallbackable(Exception error) =>\n"
        "        TransientIoErrorClassifier.IsDirectFallbackable(error);",
        text,
        count=1,
        flags=re.MULTILINE,
    )
    if count != 1:
        raise RuntimeError(f"{path}: Direct fallback block not found")
    text = text.replace(
        "ex.HResult & 0xFFFF",
        "TransientIoErrorClassifier.GetNativeCodeOrZero(ex)",
    )
    write(path, text)

# Adaptive negative feedback. AcquireIoAsync already stops new admissions after
# the target falls below current outstanding I/O, so this naturally drains.
scheduler_path = "dotnet/RepartoCopier.Core/DeviceScheduler.cs"
scheduler = read(scheduler_path)
anchor = "    public void ReserveBacklog(int bytes)\n    {"
if anchor not in scheduler:
    raise RuntimeError("DeviceScheduler ReserveBacklog anchor missing")
method = '''    internal bool RecordTransientFailure()
    {
        lock (_ioGate)
        {
            ThrowIfDisposed();
            var previous = _currentQueueDepth;
            var bestKnown = Math.Max(1, Math.Min(_bestObservedQueueDepth, previous));
            var next = bestKnown < previous ? bestKnown : Math.Max(1, previous / 2);

            ResetSampleLocked();
            if (next >= previous)
            {
                _lastQueueDepthDecision = "hold:transient-at-minimum";
                return false;
            }

            _currentQueueDepth = next;
            _queueDepthDownshifts++;
            _lastQueueDepthDecision = "decrease:transient-failure";
            if (next < _minimumObservedQueueDepth)
                _minimumObservedQueueDepth = next;
            return true;
        }
    }

'''
scheduler = scheduler.replace(anchor, method + anchor, 1)
write(scheduler_path, scheduler)

# Replay remains enabled by default but gains an explicit A/B switch.
replace_once(
    "dotnet/RepartoCopier.Core/Models.cs",
    '''public sealed record CopyOptions(
    bool Verify = false,
    bool SkipSame = true,
    bool KeepGoing = false);''',
    '''public sealed record CopyOptions(
    bool Verify = false,
    bool SkipSame = true,
    bool KeepGoing = false,
    bool EnableReplay = true);''',
)

# Exact recent I/O recovery diagnostics.
telemetry_path = "dotnet/RepartoCopier.Core/CopyTelemetry.cs"
telemetry = read(telemetry_path)
telemetry = telemetry.replace(
    "using System.Diagnostics;\n",
    "using System.Collections.Concurrent;\nusing System.Diagnostics;\n",
    1,
)
telemetry = telemetry.replace(
    "public enum VerificationBottleneckKind\n",
    '''public sealed record IoRecoveryEvent(
    DateTimeOffset Timestamp,
    string Phase,
    string Path,
    string Mode,
    int NativeErrorCode,
    int QueueDepth,
    int RetryCount,
    long Offset,
    bool Recovered);

public enum VerificationBottleneckKind
''',
    1,
)
telemetry = telemetry.replace(
    "    public int MaximumTransferBytes { get; init; }\n",
    "    public int MaximumTransferBytes { get; init; }\n"
    "    public IReadOnlyList<IoRecoveryEvent> RecentIoRecoveryEvents { get; init; } = [];\n",
    1,
)
telemetry = telemetry.replace(
    "    private readonly SlidingByteRateWindow _writeRate = new();\n",
    "    private readonly SlidingByteRateWindow _writeRate = new();\n"
    "    private readonly ConcurrentQueue<IoRecoveryEvent> _ioRecoveryEvents = new();\n",
    1,
)
telemetry = telemetry.replace(
    "    internal void RecordDirectDestinationFallback() => Interlocked.Increment(ref _directDestinationFallbacks);\n",
    '''    internal void RecordDirectDestinationFallback() => Interlocked.Increment(ref _directDestinationFallbacks);
    internal void RecordIoRecovery(
        string phase,
        string path,
        string mode,
        int nativeErrorCode,
        int queueDepth,
        int retryCount,
        long offset,
        bool recovered)
    {
        _ioRecoveryEvents.Enqueue(new IoRecoveryEvent(
            DateTimeOffset.UtcNow,
            phase,
            path,
            mode,
            nativeErrorCode,
            Math.Max(1, queueDepth),
            Math.Max(0, retryCount),
            offset,
            recovered));
        while (_ioRecoveryEvents.Count > 128 && _ioRecoveryEvents.TryDequeue(out _)) { }
    }
''',
    1,
)
telemetry = telemetry.replace(
    "            MaximumTransferBytes = Volatile.Read(ref _maximumTransferBytes),\n",
    "            MaximumTransferBytes = Volatile.Read(ref _maximumTransferBytes),\n"
    "            RecentIoRecoveryEvents = _ioRecoveryEvents.ToArray(),\n",
    1,
)
write(telemetry_path, telemetry)

# COPY path: eliminate fixed retry count and fixed sleeps.
engine_path = "dotnet/RepartoCopier.Core/CopyEngine.cs"
engine = read(engine_path)
if "private const int Retries = 2;" not in engine:
    raise RuntimeError("Legacy Retries constant missing before cut")
engine = engine.replace("\n\n    private const int Retries = 2;\n", "\n", 1)
old_replay = "var replayDirectory = BranchReplayPlacement.ResolveSafeDirectory(copy.SourceDevice, copy.DestinationDevices);"
if old_replay not in engine:
    raise RuntimeError("Replay directory production call missing")
engine = engine.replace(
    old_replay,
    "var replayDirectory = options.EnableReplay\n"
    "                ? BranchReplayPlacement.ResolveSafeDirectory(copy.SourceDevice, copy.DestinationDevices)\n"
    "                : null;",
    1,
)

direct_old = '''                var started = Stopwatch.GetTimestamp();
                int operations;
                try
                {
                    operations = await direct.WriteAsync(
                        data,
                        offset,
                        current.Entry.Size,
                        payloadIsAligned: true,
                        worker.DeviceScheduler,
                        job.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (DirectIoDestinationWriter.IsFallbackable(ex))
                {
                    current.RequestDirectFallback();
                    return PendingWriteResult.NeedsBufferedRetry(block, offset, ex);
                }
'''
direct_new = '''                var started = Stopwatch.GetTimestamp();
                var directRetryCount = 0;
                var lastTransientCode = 0;
                int operations;
                while (true)
                {
                    try
                    {
                        operations = await direct.WriteAsync(
                            data,
                            offset,
                            current.Entry.Size,
                            payloadIsAligned: true,
                            worker.DeviceScheduler,
                            job.Token).ConfigureAwait(false);
                        if (directRetryCount > 0)
                            job.Telemetry.RecordIoRecovery(
                                "copy-write", current.Entry.RelativePath, "direct", lastTransientCode,
                                worker.DeviceScheduler.CurrentQueueDepth, directRetryCount, offset, recovered: true);
                        break;
                    }
                    catch (Exception ex) when (TransientIoErrorClassifier.IsTransient(ex))
                    {
                        var failedQueueDepth = worker.DeviceScheduler.CurrentQueueDepth;
                        lastTransientCode = TransientIoErrorClassifier.GetNativeCodeOrZero(ex);
                        directRetryCount++;
                        worker.Progress.AddRetry();
                        job.Telemetry.RecordIoRecovery(
                            "copy-write", current.Entry.RelativePath, "direct", lastTransientCode,
                            failedQueueDepth, directRetryCount, offset, recovered: false);
                        if (!worker.DeviceScheduler.RecordTransientFailure())
                        {
                            ReleaseBranchBlock(worker, block);
                            return PendingWriteResult.Failed(ex);
                        }
                    }
                    catch (Exception ex) when (DirectIoDestinationWriter.IsFallbackable(ex))
                    {
                        current.RequestDirectFallback();
                        return PendingWriteResult.NeedsBufferedRetry(block, offset, ex);
                    }
                }
'''
if direct_old not in engine:
    raise RuntimeError("Direct write production block missing")
engine = engine.replace(direct_old, direct_new, 1)

buffered_pattern = re.compile(
    r'''        for \(var attempt = 0; attempt <= Retries; attempt\+\+\)\n        \{\n            try\n            \{\n(?P<body>.*?)                return PendingWriteResult\.Success\(\);\n            \}\n            catch \(Exception ex\)\n            \{\n                last = ex;\n                if \(attempt >= Retries \|\| !TransientIoErrorClassifier\.IsTransient\(ex\)\)\n                    break;\n                worker\.Progress\.AddRetry\(\);\n                try\n                \{\n                    await Task\.Delay\(75 \* \(attempt \+ 1\), job\.Token\)\.ConfigureAwait\(false\);\n                \}\n                catch \(Exception delayError\)\n                \{\n                    last = delayError;\n                    break;\n                \}\n            \}\n        \}\n''',
    re.DOTALL,
)
match = buffered_pattern.search(engine)
if not match:
    raise RuntimeError("Buffered fixed retry production block missing")
body = match.group("body")
buffered_new = '''        var bufferedRetryCount = 0;
        var lastBufferedTransientCode = 0;
        while (true)
        {
            try
            {
''' + body + '''                if (bufferedRetryCount > 0)
                    job.Telemetry.RecordIoRecovery(
                        "copy-write", current.Entry.RelativePath, "buffered", lastBufferedTransientCode,
                        worker.DeviceScheduler.CurrentQueueDepth, bufferedRetryCount, offset, recovered: true);
                return PendingWriteResult.Success();
            }
            catch (Exception ex)
            {
                last = ex;
                if (!TransientIoErrorClassifier.IsTransient(ex))
                    break;

                var failedQueueDepth = worker.DeviceScheduler.CurrentQueueDepth;
                lastBufferedTransientCode = TransientIoErrorClassifier.GetNativeCodeOrZero(ex);
                bufferedRetryCount++;
                worker.Progress.AddRetry();
                job.Telemetry.RecordIoRecovery(
                    "copy-write", current.Entry.RelativePath, "buffered", lastBufferedTransientCode,
                    failedQueueDepth, bufferedRetryCount, offset, recovered: false);
                if (!worker.DeviceScheduler.RecordTransientFailure())
                    break;
            }
        }
'''
engine = engine[: match.start()] + buffered_new + engine[match.end() :]
write(engine_path, engine)

# VERIFY path: identical adaptive recovery semantics for Direct and buffered reads.
verify_path = "dotnet/RepartoCopier.Core/FastVerificationReader.cs"
verify = read(verify_path)
old = "ReadDirectAsync(session, scheduler, lease, requestSize, offset, job.Token)"
if old not in verify:
    raise RuntimeError("Verify Direct call missing")
verify = verify.replace(
    old,
    "ReadDirectAsync(session, scheduler, lease, requestSize, offset, path, job, progress)",
    1,
)
old = '''ReadBufferedAsync(
                            handle,
                            scheduler,
                            lease,
                            expected.Length,
                            offset,
                            job.Token)'''
if old not in verify:
    raise RuntimeError("Verify buffered call missing")
verify = verify.replace(
    old,
    '''ReadBufferedAsync(
                            handle,
                            scheduler,
                            lease,
                            expected.Length,
                            offset,
                            path,
                            job,
                            progress)''',
    1,
)
old_direct = '''    private static async Task<int> ReadDirectAsync(
        DirectIoSourceReader.OverlappedSession session,
        DeviceScheduler scheduler,
        SourceBufferLease buffer,
        int requestSize,
        long offset,
        CancellationToken token)
    {
        using var io = await scheduler.AcquireIoAsync(requestSize, token).ConfigureAwait(false);
        return await session.ReadAsync(buffer, requestSize, offset, token).ConfigureAwait(false);
    }
'''
new_direct = '''    private static async Task<int> ReadDirectAsync(
        DirectIoSourceReader.OverlappedSession session,
        DeviceScheduler scheduler,
        SourceBufferLease buffer,
        int requestSize,
        long offset,
        string path,
        CopyJob job,
        DestinationProgress progress)
    {
        var retries = 0;
        var lastCode = 0;
        while (true)
        {
            using var io = await scheduler.AcquireIoAsync(requestSize, job.Token).ConfigureAwait(false);
            try
            {
                var read = await session.ReadAsync(buffer, requestSize, offset, job.Token).ConfigureAwait(false);
                if (retries > 0)
                    job.Telemetry.RecordIoRecovery(
                        "verify-read", path, "direct", lastCode,
                        scheduler.CurrentQueueDepth, retries, offset, recovered: true);
                return read;
            }
            catch (Exception ex) when (TransientIoErrorClassifier.IsTransient(ex))
            {
                var failedQueueDepth = scheduler.CurrentQueueDepth;
                lastCode = TransientIoErrorClassifier.GetNativeCodeOrZero(ex);
                retries++;
                progress.AddRetry();
                job.Telemetry.RecordIoRecovery(
                    "verify-read", path, "direct", lastCode,
                    failedQueueDepth, retries, offset, recovered: false);
                if (!scheduler.RecordTransientFailure())
                    throw;
            }
        }
    }
'''
if old_direct not in verify:
    raise RuntimeError("Verify Direct helper missing")
verify = verify.replace(old_direct, new_direct, 1)
old_buffered = '''    private static async Task<int> ReadBufferedAsync(
        SafeFileHandle handle,
        DeviceScheduler scheduler,
        SourceBufferLease buffer,
        int requestSize,
        long offset,
        CancellationToken token)
    {
        using var io = await scheduler.AcquireIoAsync(requestSize, token).ConfigureAwait(false);
        return await RandomAccess.ReadAsync(handle, buffer.Memory[..requestSize], offset, token).ConfigureAwait(false);
    }
'''
new_buffered = '''    private static async Task<int> ReadBufferedAsync(
        SafeFileHandle handle,
        DeviceScheduler scheduler,
        SourceBufferLease buffer,
        int requestSize,
        long offset,
        string path,
        CopyJob job,
        DestinationProgress progress)
    {
        var retries = 0;
        var lastCode = 0;
        while (true)
        {
            using var io = await scheduler.AcquireIoAsync(requestSize, job.Token).ConfigureAwait(false);
            try
            {
                var read = await RandomAccess.ReadAsync(
                    handle, buffer.Memory[..requestSize], offset, job.Token).ConfigureAwait(false);
                if (retries > 0)
                    job.Telemetry.RecordIoRecovery(
                        "verify-read", path, "buffered", lastCode,
                        scheduler.CurrentQueueDepth, retries, offset, recovered: true);
                return read;
            }
            catch (Exception ex) when (TransientIoErrorClassifier.IsTransient(ex))
            {
                var failedQueueDepth = scheduler.CurrentQueueDepth;
                lastCode = TransientIoErrorClassifier.GetNativeCodeOrZero(ex);
                retries++;
                progress.AddRetry();
                job.Telemetry.RecordIoRecovery(
                    "verify-read", path, "buffered", lastCode,
                    failedQueueDepth, retries, offset, recovered: false);
                if (!scheduler.RecordTransientFailure())
                    throw;
            }
        }
    }
'''
if old_buffered not in verify:
    raise RuntimeError("Verify buffered helper missing")
verify = verify.replace(old_buffered, new_buffered, 1)
write(verify_path, verify)

# UI: verification is enabled by default but configurable for benchmarks/max throughput.
xaml_path = "dotnet/RepartoCopier.WinUI/MainWindow.xaml"
xaml = read(xaml_path)
old = '''                        <CheckBox x:Name="SkipSameCheck" Content="Omitir archivos iguales" />
                        <CheckBox x:Name="KeepGoingCheck" Content="Continuar si un destino falla" />'''
new = '''                        <CheckBox x:Name="VerifyCheck" Content="Verificar después de copiar" IsChecked="True" />
                        <CheckBox x:Name="SkipSameCheck" Content="Omitir archivos iguales" />
                        <CheckBox x:Name="KeepGoingCheck" Content="Continuar si un destino falla" />'''
if old not in xaml:
    raise RuntimeError("Preparation checkboxes anchor missing")
xaml = xaml.replace(old, new, 1)
write(xaml_path, xaml)

ui_path = "dotnet/RepartoCopier.WinUI/MainWindow.xaml.cs"
ui = read(ui_path)
if "Verify: true," not in ui:
    raise RuntimeError("Forced Verify:true missing")
ui = ui.replace("Verify: true,", "Verify: VerifyCheck.IsChecked == true,", 1)
old = "        SkipSameCheck.IsEnabled = enabled;\n        KeepGoingCheck.IsEnabled = enabled;"
new = "        VerifyCheck.IsEnabled = enabled;\n        SkipSameCheck.IsEnabled = enabled;\n        KeepGoingCheck.IsEnabled = enabled;"
if old not in ui:
    raise RuntimeError("SetEditingEnabled checkbox anchor missing")
ui = ui.replace(old, new, 1)
old = '            OverallDetailText.Text = "Preparando copia con verificación rápida...";'
new = '''            OverallDetailText.Text = options.Verify
                ? "Preparando copia con verificación rápida..."
                : "Preparando copia sin verificación posterior...";'''
if old not in ui:
    raise RuntimeError("Preparation verification status anchor missing")
ui = ui.replace(old, new, 1)
write(ui_path, ui)

# New architecture/regression contract independent of implementation-private source layout.
write(
    "dotnet/RepartoCopier.Core.Tests/IoRecoveryArchitectureTests.cs",
    '''using Microsoft.VisualStudio.TestTools.UnitTesting;
using RepartoCopier.Core;

namespace RepartoCopier.Core.Tests;

[TestClass]
public sealed class IoRecoveryArchitectureTests
{
    [TestMethod]
    public void ManagedNonWin32IOExceptionIsNotMisclassified()
    {
        var error = new IOException("managed", unchecked((int)0x80131500));
        Assert.IsFalse(TransientIoErrorClassifier.TryGetNativeCode(error, out _));
        Assert.IsFalse(TransientIoErrorClassifier.IsTransient(error));
    }

    [TestMethod]
    public void DirectReadAndWriteUseUnifiedNativeClassification()
    {
        var write = new DirectIoDestinationWriter.DirectIoWriteException(121, "synthetic");
        var read = new DirectIoSourceReader.DirectIoReadException(1237, "synthetic");
        Assert.IsTrue(TransientIoErrorClassifier.IsTransient(write));
        Assert.IsTrue(TransientIoErrorClassifier.IsTransient(read));
        Assert.AreEqual(121, TransientIoErrorClassifier.GetNativeCodeOrZero(write));
        Assert.AreEqual(1237, TransientIoErrorClassifier.GetNativeCodeOrZero(read));
    }

    [TestMethod]
    public void TransientFailuresConvergeNaturallyToQueueDepthOne()
    {
        using var scheduler = new DeviceScheduler("synthetic", 8, 64L * 1024 * 1024);
        Assert.IsTrue(scheduler.RecordTransientFailure());
        Assert.AreEqual(4, scheduler.CurrentQueueDepth);
        Assert.IsTrue(scheduler.RecordTransientFailure());
        Assert.AreEqual(2, scheduler.CurrentQueueDepth);
        Assert.IsTrue(scheduler.RecordTransientFailure());
        Assert.AreEqual(1, scheduler.CurrentQueueDepth);
        Assert.IsFalse(scheduler.RecordTransientFailure());
        Assert.AreEqual(1, scheduler.CurrentQueueDepth);
    }

    [TestMethod]
    public void ReplayCanBeDisabledForPhysicalABWithoutChangingDefault()
    {
        Assert.IsTrue(new CopyOptions().EnableReplay);
        Assert.IsFalse(new CopyOptions(EnableReplay: false).EnableReplay);
    }

    [TestMethod]
    public void DiagnosticsExposeRecoveryContext()
    {
        var telemetry = new CopyTelemetry();
        telemetry.RecordIoRecovery("verify-read", "D:\\x.bin", "direct", 121, 16, 1, 4096, false);
        var item = telemetry.Snapshot().RecentIoRecoveryEvents.Single();
        Assert.AreEqual("verify-read", item.Phase);
        Assert.AreEqual("direct", item.Mode);
        Assert.AreEqual(121, item.NativeErrorCode);
        Assert.AreEqual(16, item.QueueDepth);
        Assert.AreEqual(1, item.RetryCount);
        Assert.AreEqual(4096L, item.Offset);
        Assert.IsFalse(item.Recovered);
    }

    [TestMethod]
    public void ProductivePathsDoNotContainLegacyFixedRetryDelayPolicy()
    {
        var root = FindRepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "CopyEngine.cs"));
        var verify = File.ReadAllText(Path.Combine(root, "dotnet", "RepartoCopier.Core", "FastVerificationReader.cs"));
        Assert.IsFalse(engine.Contains("private const int Retries = 2", StringComparison.Ordinal));
        Assert.IsFalse(engine.Contains("Task.Delay(75 *", StringComparison.Ordinal));
        Assert.IsTrue(engine.Contains("RecordTransientFailure", StringComparison.Ordinal));
        Assert.IsTrue(verify.Contains("RecordTransientFailure", StringComparison.Ordinal));
        Assert.IsTrue(verify.Contains("TransientIoErrorClassifier.IsTransient", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RepartoCopier.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new AssertFailedException("No se encontró la raíz del repositorio.");
    }
}
''',
)

# The migration commit must not leave migration machinery behind.
Path(".github/workflows/io-recovery-migration.yml").unlink(missing_ok=True)
Path(".github/scripts/io_recovery_migration.py").unlink(missing_ok=True)
