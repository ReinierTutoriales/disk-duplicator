from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

def replace_once(old: str, new: str, label: str):
    global s
    if s.count(old) != 1:
        raise SystemExit(f'{label}: expected exactly one anchor, found {s.count(old)}')
    s = s.replace(old, new, 1)

# Per-job diagnostics object. Stopwatch/Interlocked only; no logging/string allocation in the hot path.
replace_once(
'''    private readonly IReadOnlyList<DestinationProgress> _progress;
    private Task _completion = Task.CompletedTask;

    internal CopyJob(IReadOnlyList<DestinationProgress> progress) => _progress = progress;''',
'''    private readonly IReadOnlyList<DestinationProgress> _progress;
    private readonly CopyTelemetry _telemetry = new();
    private Task _completion = Task.CompletedTask;

    internal CopyJob(IReadOnlyList<DestinationProgress> progress) => _progress = progress;''',
'copyjob telemetry field')
replace_once(
'''    public IReadOnlyList<DestinationSnapshot> Snapshot() =>
        _progress.Select(item => item.Snapshot()).ToArray();''',
'''    public IReadOnlyList<DestinationSnapshot> Snapshot() =>
        _progress.Select(item => item.Snapshot()).ToArray();

    public CopyDiagnosticsSnapshot DiagnosticsSnapshot() => _telemetry.Snapshot();
    internal CopyTelemetry Telemetry => _telemetry;''',
'copyjob diagnostics api')

# Sequential source budget/read/hash/delivery.
replace_once(
'''            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);
            pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));''',
'''            await bufferBudget.AcquireAsync(readBufferSize, job.Token).ConfigureAwait(false);
            var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
            pipeline.RecordBudgetWait(budgetElapsed);
            job.Telemetry.RecordBufferWait(budgetElapsed);
            job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);''',
'sequential budget')
replace_once(
'''                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);
                pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));''',
'''                read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), job.Token).ConfigureAwait(false);
                var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                pipeline.RecordSourceRead(readElapsed);
                job.Telemetry.RecordSourceRead(read, readElapsed);''',
'sequential source read')
replace_once(
'''            totalRead += read;
            hasher.Update(rented.AsSpan(0, read));
            var recipients = active.Where(worker => worker.IsActive).ToArray();''',
'''            totalRead += read;
            var hashStarted = Stopwatch.GetTimestamp();
            hasher.Update(rented.AsSpan(0, read));
            job.Telemetry.RecordSourceHash(read, Stopwatch.GetElapsedTime(hashStarted));
            var recipients = active.Where(worker => worker.IsActive).ToArray();''',
'sequential source hash')

# Two source delivery sites (sequential + prefetched consumer).
old_delivery = '''            await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);
            pipeline.RecordDeliveryWait(Stopwatch.GetElapsedTime(deliveryStarted));'''
replace_once(old_delivery,
'''            await DeliverAsync(recipients, new DataMessage(block), countsData: true, job).ConfigureAwait(false);
            var deliveryElapsed = Stopwatch.GetElapsedTime(deliveryStarted);
            pipeline.RecordDeliveryWait(deliveryElapsed);
            job.Telemetry.RecordFanoutWait(deliveryElapsed);''',
'sequential delivery')
replace_once(
'''                await DeliverAsync(recipients, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);
                pipeline.RecordDeliveryWait(Stopwatch.GetElapsedTime(deliveryStarted));''',
'''                await DeliverAsync(recipients, new DataMessage(shared), countsData: true, job).ConfigureAwait(false);
                var deliveryElapsed = Stopwatch.GetElapsedTime(deliveryStarted);
                pipeline.RecordDeliveryWait(deliveryElapsed);
                job.Telemetry.RecordFanoutWait(deliveryElapsed);''',
'prefetched delivery')

# Prefetch producer budget/read/hash.
replace_once(
'''                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);
                    pipeline.RecordBudgetWait(Stopwatch.GetElapsedTime(budgetStarted));''',
'''                    await bufferBudget.AcquireAsync(readBufferSize, token).ConfigureAwait(false);
                    var budgetElapsed = Stopwatch.GetElapsedTime(budgetStarted);
                    pipeline.RecordBudgetWait(budgetElapsed);
                    job.Telemetry.RecordBufferWait(budgetElapsed);
                    job.Telemetry.ObserveBuffer(bufferBudget.UsedBytes, bufferBudget.TargetBytes);''',
'prefetch budget')
replace_once(
'''                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);
                    pipeline.RecordSourceRead(Stopwatch.GetElapsedTime(readStarted));''',
'''                    read = await source.ReadAsync(rented.AsMemory(0, readBufferSize), token).ConfigureAwait(false);
                    var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                    pipeline.RecordSourceRead(readElapsed);
                    job.Telemetry.RecordSourceRead(read, readElapsed);''',
'prefetch source read')
replace_once(
'''                totalRead += read;
                hasher.Update(rented.AsSpan(0, read));
                var block = new SourceReadBlock(rented, read, readBufferSize, bufferBudget);''',
'''                totalRead += read;
                var hashStarted = Stopwatch.GetTimestamp();
                hasher.Update(rented.AsSpan(0, read));
                job.Telemetry.RecordSourceHash(read, Stopwatch.GetElapsedTime(hashStarted));
                var block = new SourceReadBlock(rented, read, readBufferSize, bufferBudget);''',
'prefetch source hash')

# Per-destination backpressure wait. This is the producer stall signal.
replace_once(
'''            if (!await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false))
            {
                ReleaseIfData(message);
                return;
            }''',
'''            var queueWaitStarted = Stopwatch.GetTimestamp();
            var windowOpen = await worker.WaitForAdaptiveWindowAsync(job.Token).ConfigureAwait(false);
            job.Telemetry.RecordQueueWait(Stopwatch.GetElapsedTime(queueWaitStarted));
            if (!windowOpen)
            {
                ReleaseIfData(message);
                return;
            }''',
'adaptive queue wait')

# Whole shared-block destination write time (counts all block sizes).
replace_once(
'''                                if (chunkData.Block.Length >= WriteChunkSize)
                                    worker.RecordBlockWrite(elapsed);
                                current.Copied += chunkData.Block.Length;''',
'''                                if (chunkData.Block.Length >= WriteChunkSize)
                                    worker.RecordBlockWrite(elapsed);
                                job.Telemetry.RecordWrite(chunkData.Block.Length, elapsed);
                                current.Copied += chunkData.Block.Length;''',
'writer block timing')

# FinishFile needs job telemetry.
replace_once(
'                        FinishFile(worker, current, end.Hash, options, recovery);',
'                        FinishFile(worker, current, end.Hash, options, recovery, job);',
'FinishFile call')
replace_once(
'''        byte[] expectedHash,
        CopyOptions options,
        RecoveryCheckpointWriter recovery)''',
'''        byte[] expectedHash,
        CopyOptions options,
        RecoveryCheckpointWriter recovery,
        CopyJob job)''',
'FinishFile signature')
replace_once(
'''            // BufferSize=1 disables FileStream buffering; one durable flush is enough.
            current.Stream.Flush(flushToDisk: true);
            current.Stream.Dispose();''',
'''            // BufferSize=1 disables FileStream buffering; one durable flush is enough.
            var flushStarted = Stopwatch.GetTimestamp();
            current.Stream.Flush(flushToDisk: true);
            job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            current.Stream.Dispose();''',
'durable flush')
replace_once(
'''        ValidateRuntimeDestinationPath(worker.Root, current.Entry.RelativePath);
        CommitPart(current.PartPath, current.DestinationPath, current.BackupPath);
        File.SetLastWriteTimeUtc(current.DestinationPath, current.Entry.LastWriteTimeUtc);
        recovery.Append(''',
'''        ValidateRuntimeDestinationPath(worker.Root, current.Entry.RelativePath);
        var commitStarted = Stopwatch.GetTimestamp();
        CommitPart(current.PartPath, current.DestinationPath, current.BackupPath);
        job.Telemetry.RecordCommit(Stopwatch.GetElapsedTime(commitStarted));
        File.SetLastWriteTimeUtc(current.DestinationPath, current.Entry.LastWriteTimeUtc);
        var recoveryStarted = Stopwatch.GetTimestamp();
        recovery.Append(''',
'commit/recovery start')
replace_once(
'''                current.Entry.ModifiedUnixNanoseconds),
            expectedHash);
        worker.Progress.MarkDone();''',
'''                current.Entry.ModifiedUnixNanoseconds),
            expectedHash);
        job.Telemetry.RecordRecovery(Stopwatch.GetElapsedTime(recoveryStarted));
        worker.Progress.MarkDone();''',
'recovery end')

# Final verification read/hash gets its own counters. SkipSame remains excluded from verify metrics.
replace_once(
'                var actual = await HashFileAsync(destination, job.Token, resources).ConfigureAwait(false);',
'                var actual = await HashFileAsync(destination, job.Token, resources, job.Telemetry, verification: true).ConfigureAwait(false);',
'verify hash call')
replace_once(
'''        string path,
        CancellationToken token,
        ResourceGovernor? resources = null)''',
'''        string path,
        CancellationToken token,
        ResourceGovernor? resources = null,
        CopyTelemetry? telemetry = null,
        bool verification = false)''',
'HashFile signature')
replace_once(
'''            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0) break;
                if (resources is null)
                {
                    hasher.Update(buffer.AsSpan(0, read));
                }
                else
                {
                    using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
                    hasher.Update(buffer.AsSpan(0, read));
                }
            }''',
'''            while (true)
            {
                var readStarted = Stopwatch.GetTimestamp();
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                var readElapsed = Stopwatch.GetElapsedTime(readStarted);
                if (verification)
                    telemetry?.RecordVerifyRead(read, readElapsed);
                if (read == 0) break;
                var hashStarted = Stopwatch.GetTimestamp();
                if (resources is null)
                {
                    hasher.Update(buffer.AsSpan(0, read));
                }
                else
                {
                    using var lease = await resources.EnterCpuWorkAsync(token).ConfigureAwait(false);
                    hasher.Update(buffer.AsSpan(0, read));
                }
                if (verification)
                    telemetry?.RecordVerifyHash(read, Stopwatch.GetElapsedTime(hashStarted));
            }''',
'HashFile loop')

# Expose current adaptive byte-budget target read-only for diagnostics.
replace_once(
'''        internal long UsedBytes
        {
            get { lock (_gate) return _usedBytes; }
        }''',
'''        internal long UsedBytes
        {
            get { lock (_gate) return _usedBytes; }
        }

        internal long TargetBytes
        {
            get { lock (_gate) return _targetBytes; }
        }''',
'byte budget target')

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
    private long _sourceReadBytes, _sourceReadTicks;
    private long _sourceHashBytes, _sourceHashTicks;
    private long _bufferWaitTicks, _fanoutWaitTicks, _queueWaitTicks;
    private long _writtenBytes, _writeTicks;
    private int _flushes, _commits, _recoveryEvents;
    private long _flushTicks, _commitTicks, _recoveryTicks;
    private long _verifyReadBytes, _verifyReadTicks;
    private long _verifyHashBytes, _verifyHashTicks;
    private long _peakBufferedBytes, _maxObservedBufferTargetBytes;

    internal void RecordSourceRead(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceReadBytes, bytes); AddTicks(ref _sourceReadTicks, elapsed); }
    internal void RecordSourceHash(int bytes, TimeSpan elapsed) { AddBytes(ref _sourceHashBytes, bytes); AddTicks(ref _sourceHashTicks, elapsed); }
    internal void RecordBufferWait(TimeSpan elapsed) => AddTicks(ref _bufferWaitTicks, elapsed);
    internal void RecordFanoutWait(TimeSpan elapsed) => AddTicks(ref _fanoutWaitTicks, elapsed);
    internal void RecordQueueWait(TimeSpan elapsed) => AddTicks(ref _queueWaitTicks, elapsed);
    internal void RecordWrite(int bytes, TimeSpan elapsed) { AddBytes(ref _writtenBytes, bytes); AddTicks(ref _writeTicks, elapsed); }
    internal void RecordFlush(TimeSpan elapsed) { Interlocked.Increment(ref _flushes); AddTicks(ref _flushTicks, elapsed); }
    internal void RecordCommit(TimeSpan elapsed) { Interlocked.Increment(ref _commits); AddTicks(ref _commitTicks, elapsed); }
    internal void RecordRecovery(TimeSpan elapsed) { Interlocked.Increment(ref _recoveryEvents); AddTicks(ref _recoveryTicks, elapsed); }
    internal void RecordVerifyRead(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyReadBytes, bytes); AddTicks(ref _verifyReadTicks, elapsed); }
    internal void RecordVerifyHash(int bytes, TimeSpan elapsed) { AddBytes(ref _verifyHashBytes, bytes); AddTicks(ref _verifyHashTicks, elapsed); }

    internal void ObserveBuffer(long usedBytes, long targetBytes)
    {
        UpdateMax(ref _peakBufferedBytes, usedBytes);
        UpdateMax(ref _maxObservedBufferTargetBytes, targetBytes);
    }

    internal CopyDiagnosticsSnapshot Snapshot() => new(
        Interlocked.Read(ref _sourceReadBytes), ToTimeSpan(Interlocked.Read(ref _sourceReadTicks)),
        Interlocked.Read(ref _sourceHashBytes), ToTimeSpan(Interlocked.Read(ref _sourceHashTicks)),
        ToTimeSpan(Interlocked.Read(ref _bufferWaitTicks)),
        ToTimeSpan(Interlocked.Read(ref _fanoutWaitTicks)),
        ToTimeSpan(Interlocked.Read(ref _queueWaitTicks)),
        Interlocked.Read(ref _writtenBytes), ToTimeSpan(Interlocked.Read(ref _writeTicks)),
        Volatile.Read(ref _flushes), ToTimeSpan(Interlocked.Read(ref _flushTicks)),
        Volatile.Read(ref _commits), ToTimeSpan(Interlocked.Read(ref _commitTicks)),
        Volatile.Read(ref _recoveryEvents), ToTimeSpan(Interlocked.Read(ref _recoveryTicks)),
        Interlocked.Read(ref _verifyReadBytes), ToTimeSpan(Interlocked.Read(ref _verifyReadTicks)),
        Interlocked.Read(ref _verifyHashBytes), ToTimeSpan(Interlocked.Read(ref _verifyHashTicks)),
        Interlocked.Read(ref _peakBufferedBytes), Interlocked.Read(ref _maxObservedBufferTargetBytes),
        Stopwatch.GetElapsedTime(_started));

    private static void AddBytes(ref long target, int bytes) { if (bytes > 0) Interlocked.Add(ref target, bytes); }
    private static void AddTicks(ref long target, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        var ticks = (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
        if (ticks > 0) Interlocked.Add(ref target, ticks);
    }
    private static TimeSpan ToTimeSpan(long ticks) => ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
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

# Permanent regression for telemetry correctness.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task AdaptivePipelinePrefetchPreservesExactLargeFileFanOut()
'''
if t.count(anchor) != 1:
    raise SystemExit('telemetry test anchor mismatch')
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
        Assert.IsTrue(metrics.VerifyHashBytes >= (long)payload.Length * destinations.Length);
        Assert.IsTrue(metrics.PeakBufferedBytes > 0);
        Assert.IsTrue(metrics.MaximumObservedBufferTargetBytes >= metrics.PeakBufferedBytes);
        Assert.AreEqual(destinations.Length, metrics.DurableFlushes);
        Assert.AreEqual(destinations.Length, metrics.Commits);
        Assert.AreEqual(destinations.Length, metrics.RecoveryEvents);
    }

'''
t = t.replace(anchor, new_test + anchor, 1)
tests.write_text(t, encoding='utf-8')

# Runner-only stress matrix. Its throughput is for relative software bottleneck analysis,
# not a claim about physical USB/SATA/NVMe bandwidth.
stress_dir = Path('.github/stress-runtime')
stress_dir.mkdir(parents=True, exist_ok=True)
(stress_dir / 'RepartoCopier.Stress.csproj').write_text(r'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="../../dotnet/RepartoCopier.Core/RepartoCopier.Core.csproj" /></ItemGroup>
</Project>
''', encoding='utf-8')
(stress_dir / 'Program.cs').write_text(r'''using System.Diagnostics;
using System.Text.Json;
using RepartoCopier.Core;

static async Task CreateFile(string path, long bytes, int seed)
{
    var random = new Random(seed);
    var buffer = new byte[4 * 1024 * 1024];
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
    long written = 0;
    while (written < bytes)
    {
        random.NextBytes(buffer);
        var count = (int)Math.Min(buffer.Length, bytes - written);
        await stream.WriteAsync(buffer.AsMemory(0, count));
        written += count;
    }
}
static void Healthy(CopyJob job)
{
    foreach (var s in job.Snapshot()) if (s.Phase != DestinationPhase.Done) throw new Exception($"{s.Label}: {s.Phase}: {s.Error}");
}
static async Task Run(string name, Func<string, Task<string>> make, int n, bool verify)
{
    var root = Path.Combine(Path.GetTempPath(), "RepartoCopier-Stress", $"{name}-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
    try
    {
        var source = await make(root);
        var destinations = Enumerable.Range(0, n).Select(i => Directory.CreateDirectory(Path.Combine(root, $"d{i}")).FullName).ToArray();
        var wall = Stopwatch.StartNew();
        await using var job = CopyEngine.Start(CopyPlan.Create(source, destinations, false, false), new CopyOptions(verify, false, false));
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(10)); wall.Stop(); Healthy(job);
        Console.WriteLine($"STRESS|{name}|n={n}|verify={verify}|wall_ms={wall.Elapsed.TotalMilliseconds:F0}|{JsonSerializer.Serialize(job.DiagnosticsSnapshot())}");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}
await Run("large-single", async r => { var s=Directory.CreateDirectory(Path.Combine(r,"source")).FullName; await CreateFile(Path.Combine(s,"x.bin"),512L*1024*1024,1); return s; },1,false);
await Run("large-fanout", async r => { var s=Directory.CreateDirectory(Path.Combine(r,"source")).FullName; await CreateFile(Path.Combine(s,"x.bin"),256L*1024*1024,2); return s; },4,false);
await Run("medium-files", async r => { var s=Directory.CreateDirectory(Path.Combine(r,"source")).FullName; for(int i=0;i<256;i++) await CreateFile(Path.Combine(s,$"m{i:D3}.bin"),1024L*1024,1000+i); return s; },4,false);
await Run("small-files", async r => { var s=Directory.CreateDirectory(Path.Combine(r,"source")).FullName; var b=new byte[4096]; var rnd=new Random(3); for(int i=0;i<4096;i++){rnd.NextBytes(b); await File.WriteAllBytesAsync(Path.Combine(s,$"s{i:D4}.bin"),b);} return s; },4,false);
await Run("verify-fanout", async r => { var s=Directory.CreateDirectory(Path.Combine(r,"source")).FullName; await CreateFile(Path.Combine(s,"x.bin"),256L*1024*1024,4); return s; },4,true);
''', encoding='utf-8')
