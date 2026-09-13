from pathlib import Path

root = Path(__file__).resolve().parents[1]
engine_path = root / 'dotnet/RepartoCopier.Core/CopyEngine.cs'
telemetry_path = root / 'dotnet/RepartoCopier.Core/CopyTelemetry.cs'
test_path = root / 'dotnet/RepartoCopier.Core.Tests/ProductionFastPathTests.cs'


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f'{label}: expected 1 match, found {count}')
    return text.replace(old, new, 1)

engine = engine_path.read_text(encoding='utf-8')
engine = replace_once(
    engine,
    '    private const int WriteChunkSize = 4 * 1024 * 1024;\n',
    '',
    'remove write chunk constant',
)
engine = replace_once(
    engine,
    '    private const int WriteThroughFileThreshold = WriteChunkSize;\n',
    '    private const int WriteThroughFileThreshold = 4 * 1024 * 1024;\n',
    'write-through threshold',
)
old_loop = '''                var remaining = data;\n                while (!remaining.IsEmpty)\n                {\n                    var length = Math.Min(WriteChunkSize, remaining.Length);\n                    await current.Stream.WriteAsync(remaining[..length], job.Token).ConfigureAwait(false);\n                    remaining = remaining[length..];\n                    worker.NoteProgress();\n                }\n                return;'''
new_loop = '''                // SharedBlock is already sized for the sequential I/O pipeline. Send the\n                // whole block to FileStream in one asynchronous operation instead of\n                // fragmenting a 16 MiB block into four separately awaited 4 MiB writes.\n                // This reduces syscalls/IOCP completions and managed async overhead while\n                // preserving the existing retry boundary at current.Copied.\n                await current.Stream.WriteAsync(data, job.Token).ConfigureAwait(false);\n                job.Telemetry.RecordWriteOperation();\n                worker.NoteProgress();\n                return;'''
engine = replace_once(engine, old_loop, new_loop, 'coalesce block writes')
engine_path.write_text(engine, encoding='utf-8')

telemetry = telemetry_path.read_text(encoding='utf-8')
telemetry = replace_once(
    telemetry,
    '    long WrittenBytes,\n    TimeSpan WriteTime,\n',
    '    long WrittenBytes,\n    long WriteOperations,\n    TimeSpan WriteTime,\n',
    'snapshot write operations',
)
telemetry = replace_once(
    telemetry,
    '    private long _writtenBytes, _writeTicks;\n',
    '    private long _writtenBytes, _writeOperations, _writeTicks;\n',
    'telemetry write fields',
)
telemetry = replace_once(
    telemetry,
    '    internal void RecordWrite(int bytes, TimeSpan elapsed) { AddBytes(ref _writtenBytes, bytes); AddTicks(ref _writeTicks, elapsed); }\n',
    '    internal void RecordWrite(int bytes, TimeSpan elapsed) { AddBytes(ref _writtenBytes, bytes); AddTicks(ref _writeTicks, elapsed); }\n    internal void RecordWriteOperation() => Interlocked.Increment(ref _writeOperations);\n',
    'record write operation',
)
telemetry = replace_once(
    telemetry,
    '        Interlocked.Read(ref _writtenBytes), ToTimeSpan(Interlocked.Read(ref _writeTicks)),\n',
    '        Interlocked.Read(ref _writtenBytes), Interlocked.Read(ref _writeOperations), ToTimeSpan(Interlocked.Read(ref _writeTicks)),\n',
    'snapshot write operation value',
)
telemetry_path.write_text(telemetry, encoding='utf-8')

tests = test_path.read_text(encoding='utf-8')
marker = '''    [TestMethod]\n    public async Task ExplicitDiagnosticVerificationRemainsAvailable()\n'''
new_test = '''    [TestMethod]\n    public async Task LargeSharedBlocksAreWrittenWithOneIoOperationPerDestinationBlock()\n    {\n        using var temp = new TempDirectory();\n        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;\n        var payload = new byte[20 * 1024 * 1024 + 733];\n        new Random(20260913).NextBytes(payload);\n        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), payload);\n\n        var destinations = Enumerable.Range(0, 2)\n            .Select(index => Directory.CreateDirectory(Path.Combine(temp.Path, $"dest-write-{index}")).FullName)\n            .ToArray();\n        var plan = CopyPlan.Create(source, destinations, skipSame: false, keepGoing: false);\n\n        await using var job = CopyEngine.Start(plan);\n        await job.Completion.WaitAsync(TimeSpan.FromSeconds(60));\n\n        Assert.IsTrue(job.Snapshot().All(item => item.Phase == DestinationPhase.Done));\n        var metrics = job.DiagnosticsSnapshot();\n        // 20 MiB + 733 bytes uses two source blocks (16 MiB + tail). With two\n        // destinations the optimized writer must issue exactly four WriteAsync calls.\n        Assert.AreEqual(4L, metrics.WriteOperations);\n    }\n\n'''
if marker not in tests:
    raise SystemExit('test insertion marker not found')
tests = tests.replace(marker, new_test + marker, 1)
test_path.write_text(tests, encoding='utf-8')
