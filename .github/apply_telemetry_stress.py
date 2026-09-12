from pathlib import Path
import re

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

# Attach low-overhead per-job telemetry.
s = s.replace(
'''    private readonly IReadOnlyList<DestinationProgress> _progress;\n    private Task _completion = Task.CompletedTask;''',
'''    private readonly IReadOnlyList<DestinationProgress> _progress;\n    private readonly CopyTelemetry _telemetry = new();\n    private Task _completion = Task.CompletedTask;''', 1)
s = s.replace(
'''    public IReadOnlyList<DestinationSnapshot> Snapshot() =>\n        _progress.Select(item => item.Snapshot()).ToArray();''',
'''    public IReadOnlyList<DestinationSnapshot> Snapshot() =>\n        _progress.Select(item => item.Snapshot()).ToArray();\n\n    public CopyDiagnosticsSnapshot DiagnosticsSnapshot() => _telemetry.Snapshot();\n    internal CopyTelemetry Telemetry => _telemetry;''', 1)

# Record budget waits + observed buffer use at every source acquisition.
s = s.replace(
'''            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);\n            pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));''',
'''            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);\n            var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);\n            pipeline.RecordBudgetWait(budgetElapsed);\n            job.Telemetry.RecordBufferWait(budgetElapsed);\n            job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);''')
s = s.replace(
'''                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);\n                    pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));''',
'''                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);\n                    var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);\n                    pipeline.RecordBudgetWait(budgetElapsed);\n                    job.Telemetry.RecordBufferWait(budgetElapsed);\n                    job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);''')

# Source read telemetry: reuse existing stopwatch instead of adding an extra timestamp pair.
s = s.replace(
'''                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);\n                pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));''',
'''                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);\n                var readElapsed = Stopwatch.GetElapsedTime(readStarted);\n                pipeline.RecordSourceRead(readElapsed);\n                job.Telemetry.RecordSourceRead(read, readElapsed);''')
s = s.replace(
'''                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);\n                    pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));''',
'''                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);\n                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);\n                    pipeline.RecordSourceRead(readElapsed);\n                    job.Telemetry.RecordSourceRead(read, readElapsed);''')

# Time source BLAKE3 in both sequential and prefetched source paths only.
def instrument_hash_in_region(text, start_marker, end_marker):
    start = text.index(start_marker)
    end = text.index(end_marker, start)
    region = text[start:end]
    old = '            hasher.Update(rented.AsSpan(0, read));'
    if old not in region:
        old = '                    hasher.Update(rented.AsSpan(0, read));'
    if old not in region:
        raise SystemExit(f'hasher anchor missing in {start_marker}')
    indent = old.split('hasher')[0]
    new = (indent + 'var hashStarted = Stopwatch.GetTimestamp();\n' +
           indent + 'hasher.Update(rented.AsSpan(0, read));\n' +
           indent + 'job.Telemetry.RecordSourceHash(read, Stopwatch.GetElapsedTime(hashStarted));')
    region = region.replace(old, new, 1)
    return text[:start] + region + text[end:]

s = instrument_hash_in_region(s, '    private static async Task<SourceReadResult?> ReadAndFanOutSequentialAsync(', '    private static async Task<SourceReadResult?> ReadAndFanOutPrefetchedAsync(')
s = instrument_hash_in_region(s, '    private static async Task<SourceReadResult> PrefetchSourceAsync(', '    private static async Task DeliverAsync(')

# Delivery/backpressure timing reuses the already measured elapsed intervals.
s = s.replace(
'''            pipeline.RecordDeliveryWait(Stopwatch.GetElapsedTime(deliveryStarted));''',
'''            var deliveryElapsed = Stopwatch.GetElapsedTime(deliveryStarted);\n            pipeline.RecordDeliveryWait(deliveryElapsed);\n            job.Telemetry.RecordFanoutWait(deliveryElapsed);''')

# Measure the adaptive destination-window wait, which is the actionable backpressure signal.
needle = '        await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);'
if needle not in s:
    raise SystemExit('adaptive queue wait anchor not found')
s = s.replace(needle,
'''        var queueWaitStarted = Stopwatch.GetTimestamp();\n        await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);\n        job.Telemetry.RecordQueueWait(Stopwatch.GetElapsedTime(queueWaitStarted));''', 1)

# Reuse the existing whole-block write timer used by adaptive queue tuning.
needle = '                    worker.RecordBlockWrite(elapsed);'
if needle not in s:
    raise SystemExit('block write telemetry anchor not found')
s = s.replace(needle,
'''                    worker.RecordBlockWrite(elapsed);\n                    job.Telemetry.RecordWrite(data.Block.Length, elapsed);''', 1)

# Durable flush and transactional commit latency are separate bottlenecks for tiny-file workloads.
needle = '        current.Stream.Flush(flushToDisk: true);'
if needle not in s:
    raise SystemExit('durable flush anchor not found')
s = s.replace(needle,
'''        var flushStarted = Stopwatch.GetTimestamp();\n        current.Stream.Flush(flushToDisk: true);\n        job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));''', 1)
# FinishFile needs the job for telemetry.
s = s.replace('FinishFile(worker, current, end.Hash, recovery);', 'FinishFile(worker, current, end.Hash, recovery, job);', 1)
s = s.replace(
'''        byte[] hash,\n        RecoveryCheckpointWriter recovery)''',
'''        byte[] hash,\n        RecoveryCheckpointWriter recovery,\n        CopyJob job)''', 1)
needle = '        CommitPart(current.PartPath, current.DestinationPath, current.BackupPath);'
if needle not in s:
    raise SystemExit('commit anchor not found')
s = s.replace(needle,
'''        var commitStarted = Stopwatch.GetTimestamp();\n        CommitPart(current.PartPath, current.DestinationPath, current.BackupPath);\n        job.Telemetry.RecordCommit(Stopwatch.GetElapsedTime(commitStarted));''', 1)
# Time recovery append including any batched durable checkpoint triggered by that append.
needle = '''        recovery.Append(\n            new RecoveryFile('''
if needle not in s:
    raise SystemExit('recovery append anchor not found')
s = s.replace(needle,
'''        var recoveryStarted = Stopwatch.GetTimestamp();\n        recovery.Append(\n            new RecoveryFile(''', 1)
# Close the append call with instrumentation at the first matching hash call in FinishFile region.
start = s.index('    private static void FinishFile(')
end = s.index('    private static async Task VerifyDestinationsAsync(', start)
region = s[start:end]
anchor = '''            hash);'''
if anchor not in region:
    raise SystemExit('recovery append close anchor not found')
region = region.replace(anchor, '''            hash);\n        job.Telemetry.RecordRecovery(Stopwatch.GetElapsedTime(recoveryStarted));''', 1)
s = s[:start] + region + s[end:]

# Final verification: instrument destination read and hash separately.
s = s.replace(
'var actual = await HashFileAsync(destination, job.Token, resources).ConfigureAwait(false);',
'var actual = await HashFileAsync(destination, job.Token, resources, job.Telemetry, verification: true).ConfigureAwait(false);', 1)
s = s.replace(
'var sourceHash = await HashFileAsync(entry.SourcePath, token, resources).ConfigureAwait(false);',
'var sourceHash = await HashFileAsync(entry.SourcePath, token, resources, job.Telemetry).ConfigureAwait(false);', 1)
s = s.replace(
'var destinationHash = await HashFileAsync(destination, token, resources).ConfigureAwait(false);',
'var destinationHash = await HashFileAsync(destination, token, resources, job.Telemetry).ConfigureAwait(false);', 1)
s = s.replace(
'''        CancellationToken token,\n        ResourceGovernor? resources = null)''',
'''        CancellationToken token,\n        ResourceGovernor? resources = null,\n        CopyTelemetry? telemetry = null,\n        bool verification = false)''', 1)
# Replace read inside HashFileAsync only.
start = s.index('    private static async Task<byte[]> HashFileAsync(')
end = s.index('    private static void CommitPart(', start)
region = s[start:end]
old = '''                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);\n                if (read == 0) break;'''
new = '''                var readStarted = Stopwatch.GetTimestamp();\n                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);\n                var readElapsed = Stopwatch.GetElapsedTime(readStarted);\n                if (verification)\n                    telemetry?.RecordVerifyRead(read, readElapsed);\n                if (read == 0) break;'''
if old not in region:
    raise SystemExit('HashFile read anchor not found')
region = region.replace(old, new, 1)
# instrument both hasher branches with one timer around the actual update
old = '''                    hasher.Update(buffer.AsSpan(0, read));'''
# there are two branches; replace both
region = region.replace(old,
'''                    var hashStarted = Stopwatch.GetTimestamp();\n                    hasher.Update(buffer.AsSpan(0, read));\n                    if (verification)\n                        telemetry?.RecordVerifyHash(read, Stopwatch.GetElapsedTime(hashStarted));''')
s = s[:start] + region + s[end:]

# Expose current memory target for telemetry without changing budget policy.
needle = '''        internal long UsedBytes\n        {\n            get { lock (_gate) return _usedBytes; }\n        }'''
if needle not in s:
    raise SystemExit('AdaptiveByteBudget UsedBytes anchor not found')
s = s.replace(needle, needle + '''\n\n        internal long TargetBytes\n        {\n            get { lock (_gate) return _targetBytes; }\n        }''', 1)

engine.write_text(s, encoding='utf-8')

telemetry = r'''using System.Diagnostics;
using System.Threading;

namespace RepartoCopier.Core;

public sealed record CopyDiagnosticsSnapshot(
    long SourceReadBytes,
    TimeSpan SourceReadTime,
    long SourceHashBytes,
    TimeSpan SourceHashTime,
    TimeSpan BufferWaitTime,
    TimeSpan FanoutWaitTime,
    TimeSpan QueueWaitTime,
    long WrittenBytes,
    TimeSpan WriteTime,
    int DurableFlushes,
    TimeSpan DurableFlushTime,
    int Commits,
    TimeSpan CommitTime,
    int RecoveryEvents,
    TimeSpan RecoveryTime,
    long VerifyReadBytes,
    TimeSpan VerifyReadTime,
    long VerifyHashBytes,
    TimeSpan VerifyHashTime,
    long PeakBufferedBytes,
    long MaximumObservedBufferTargetBytes,
    TimeSpan Elapsed)
{
    public double SourceReadBytesPerSecond => Rate(SourceReadBytes, SourceReadTime);
    public double SourceHashBytesPerSecond => Rate(SourceHashBytes, SourceHashTime);
    public double WriteBytesPerSecond => Rate(WrittenBytes, WriteTime);
    public double VerifyReadBytesPerSecond => Rate(VerifyReadBytes, VerifyReadTime);
    public double VerifyHashBytesPerSecond => Rate(VerifyHashBytes, VerifyHashTime);

    private static double Rate(long bytes, TimeSpan elapsed) =>
        bytes <= 0 || elapsed <= TimeSpan.Zero ? 0 : bytes / elapsed.TotalSeconds;
}

internal sealed class CopyTelemetry
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _sourceReadBytes;
    private long _sourceReadTicks;
    private long _sourceHashBytes;
    private long _sourceHashTicks;
    private long _bufferWaitTicks;
    private long _fanoutWaitTicks;
    private long _queueWaitTicks;
    private long _writtenBytes;
    private long _writeTicks;
    private int _flushes;
    private long _flushTicks;
    private int _commits;
    private long _commitTicks;
    private int _recoveryEvents;
    private long _recoveryTicks;
    private long _verifyReadBytes;
    private long _verifyReadTicks;
    private long _verifyHashBytes;
    private long _verifyHashTicks;
    private long _peakBufferedBytes;
    private long _maxObservedBufferTargetBytes;

    internal void RecordSourceRead(int bytes, TimeSpan elapsed)
    {
        if (bytes > 0) Interlocked.Add(ref _sourceReadBytes, bytes);
        AddTicks(ref _sourceReadTicks, elapsed);
    }

    internal void RecordSourceHash(int bytes, TimeSpan elapsed)
    {
        if (bytes > 0) Interlocked.Add(ref _sourceHashBytes, bytes);
        AddTicks(ref _sourceHashTicks, elapsed);
    }

    internal void RecordBufferWait(TimeSpan elapsed) => AddTicks(ref _bufferWaitTicks, elapsed);
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);
    internal void RecordQueueWait(TimeSpan elapsed) => AddTicks(ref _queueWaitTicks, elapsed);

    internal void RecordWrite(int bytes, TimeSpan elapsed)
    {
        if (bytes > 0) Interlocked.Add(ref _writtenBytes, bytes);
        AddTicks(ref _writeTicks, elapsed);
    }

    internal void RecordFlush(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _flushes);
        AddTicks(ref _flushTicks, elapsed);
    }

    internal void RecordCommit(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _commits);
        AddTicks(ref _commitTicks, elapsed);
    }

    internal void RecordRecovery(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _recoveryEvents);
        AddTicks(ref _recoveryTicks, elapsed);
    }

    internal void RecordVerifyRead(int bytes, TimeSpan elapsed)
    {
        if (bytes > 0) Interlocked.Add(ref _verifyReadBytes, bytes);
        AddTicks(ref _verifyReadTicks, elapsed);
    }

    internal void RecordVerifyHash(int bytes, TimeSpan elapsed)
    {
        if (bytes > 0) Interlocked.Add(ref _verifyHashBytes, bytes);
        AddTicks(ref _verifyHashTicks, elapsed);
    }

    internal void ObserveBuffer(long usedBytes, long targetBytes)
    {
        UpdateMax(ref _peakBufferedBytes, usedBytes);
        UpdateMax(ref _maxObservedBufferTargetBytes, targetBytes);
    }

    internal CopyDiagnosticsSnapshot Snapshot() => new(
        Interlocked.Read(ref _sourceReadBytes),
        ToTimeSpan(Interlocked.Read(ref _sourceReadTicks)),
        Interlocked.Read(ref _sourceHashBytes),
        ToTimeSpan(Interlocked.Read(ref _sourceHashTicks)),
        ToTimeSpan(Interlocked.Read(ref _bufferWaitTicks)),
        ToTimeSpan(Interlocked.Read(ref _fanoutWaitTicks)),
        ToTimeSpan(Interlocked.Read(ref _queueWaitTicks)),
        Interlocked.Read(ref _writtenBytes),
        ToTimeSpan(Interlocked.Read(ref _writeTicks)),
        Volatile.Read(ref _flushes),
        ToTimeSpan(Interlocked.Read(ref _flushTicks)),
        Volatile.Read(ref _commits),
        ToTimeSpan(Interlocked.Read(ref _commitTicks)),
        Volatile.Read(ref _recoveryEvents),
        ToTimeSpan(Interlocked.Read(ref _recoveryTicks)),
        Interlocked.Read(ref _verifyReadBytes),
        ToTimeSpan(Interlocked.Read(ref _verifyReadTicks)),
        Interlocked.Read(ref _verifyHashBytes),
        ToTimeSpan(Interlocked.Read(ref _verifyHashTicks)),
        Interlocked.Read(ref _peakBufferedBytes),
        Interlocked.Read(ref _maxObservedBufferTargetBytes),
        Stopwatch.GetElapsedTime(_started));

    private static void AddTicks(ref long target, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        var ticks = (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
        if (ticks > 0) Interlocked.Add(ref target, ticks);
    }

    private static TimeSpan ToTimeSpan(long stopwatchTicks) =>
        stopwatchTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)stopwatchTicks / Stopwatch.Frequency);

    private static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
'''
Path('dotnet/RepartoCopier.Core/CopyTelemetry.cs').write_text(telemetry, encoding='utf-8')

# Permanent regression: telemetry must be populated without changing correctness.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]\n    public async Task AdaptivePipelinePrefetchPreservesExactLargeFileFanOut()\n'''
if anchor not in t:
    raise SystemExit('telemetry test anchor not found')
new_test = r'''    [TestMethod]
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
        Assert.IsTrue(metrics.PeakBufferedBytes > 0);
        Assert.IsTrue(metrics.MaximumObservedBufferTargetBytes >= metrics.PeakBufferedBytes);
        Assert.AreEqual(destinations.Length, metrics.DurableFlushes);
        Assert.AreEqual(destinations.Length, metrics.Commits);
    }

'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')

# Temporary stress harness: generated in the runner and intentionally not committed.
stress_dir = Path('.github/stress-runtime')
stress_dir.mkdir(parents=True, exist_ok=True)
(stress_dir / 'RepartoCopier.Stress.csproj').write_text(r'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../dotnet/RepartoCopier.Core/RepartoCopier.Core.csproj" />
  </ItemGroup>
</Project>
''', encoding='utf-8')
(stress_dir / 'Program.cs').write_text(r'''using System.Diagnostics;
using System.Text.Json;
using RepartoCopier.Core;

static async Task CreateLargeFile(string path, long bytes, int seed)
{
    var random = new Random(seed);
    var buffer = new byte[4 * 1024 * 1024];
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
    long written = 0;
    while (written < bytes)
    {
        random.NextBytes(buffer);
        var count = (int)Math.Min(buffer.Length, bytes - written);
        await stream.WriteAsync(buffer.AsMemory(0, count));
        written += count;
    }
}

static void AssertHealthy(CopyJob job)
{
    foreach (var state in job.Snapshot())
        if (state.Phase != DestinationPhase.Done)
            throw new Exception($"Copy failed: {state.Label}: {state.Phase}: {state.Error}");
}

static async Task RunScenario(string name, Func<string, Task<string>> createSource, int destinationCount, bool verify)
{
    var root = Path.Combine(Path.GetTempPath(), "RepartoCopier-Stress", $"{name}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var source = await createSource(root);
        var destinations = Enumerable.Range(0, destinationCount)
            .Select(i => Directory.CreateDirectory(Path.Combine(root, $"dest-{i}")).FullName)
            .ToArray();
        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);
        var wall = Stopwatch.StartNew();
        await using var job = CopyEngine.Start(plan, new CopyOptions(Verify: verify, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(10));
        wall.Stop();
        AssertHealthy(job);
        var metrics = job.DiagnosticsSnapshot();
        Console.WriteLine($"STRESS|{name}|destinations={destinationCount}|verify={verify}|wall_ms={wall.Elapsed.TotalMilliseconds:F0}|{JsonSerializer.Serialize(metrics)}");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

await RunScenario("large-single", async root =>
{
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    await CreateLargeFile(Path.Combine(source, "large.bin"), 512L * 1024 * 1024, 1001);
    return source;
}, 1, false);

await RunScenario("large-fanout", async root =>
{
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    await CreateLargeFile(Path.Combine(source, "large.bin"), 256L * 1024 * 1024, 1002);
    return source;
}, 4, false);

await RunScenario("medium-files", async root =>
{
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    for (var i = 0; i < 256; i++)
        await CreateLargeFile(Path.Combine(source, $"medium-{i:D3}.bin"), 1024L * 1024, 2000 + i);
    return source;
}, 4, false);

await RunScenario("small-files", async root =>
{
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    var payload = new byte[4096];
    var random = new Random(3001);
    for (var i = 0; i < 4096; i++)
    {
        random.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(source, $"small-{i:D4}.bin"), payload);
    }
    return source;
}, 4, false);

await RunScenario("verify-fanout", async root =>
{
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    await CreateLargeFile(Path.Combine(source, "verify.bin"), 256L * 1024 * 1024, 4001);
    return source;
}, 4, true);
''', encoding='utf-8')
