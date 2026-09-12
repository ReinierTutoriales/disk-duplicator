from pathlib import Path

recovery = Path('dotnet/RepartoCopier.Core/Recovery.cs')
s = recovery.read_text(encoding='utf-8')

anchor = '''internal sealed record RecoveryFile(
    string SourcePath,
    string RelativePath,
    long Size,
    long ModifiedUnixNanoseconds);

'''
if anchor not in s:
    raise SystemExit('RecoveryFile anchor not found')
checkpoint = r'''internal sealed class RecoveryCheckpointWriter : IDisposable
{
    private const int BatchFiles = 128;
    private const long MaxCheckpointMilliseconds = 1_000;

    private readonly string _destinationRoot;
    private FileStream? _manifest;
    private FileStream? _journal;
    private int _pending;
    private long _lastCheckpoint = Environment.TickCount64;
    private bool _disposed;

    internal RecoveryCheckpointWriter(string destinationRoot) =>
        _destinationRoot = destinationRoot;

    internal void Append(RecoveryFile file, ReadOnlySpan<byte> hash)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hash.Length != 32)
            throw new ArgumentException("BLAKE3 debe contener exactamente 32 bytes.", nameof(hash));

        EnsureOpen();
        var manifestLine = Encoding.UTF8.GetBytes(
            $"{Convert.ToHexString(hash).ToLowerInvariant()}  {RecoveryManager.ManifestKey(file.RelativePath)}{Environment.NewLine}");
        var journalLine = Encoding.UTF8.GetBytes(
            $"{{\"key\":\"{RecoveryManager.StateKey(file)}\"}}{Environment.NewLine}");
        _manifest!.Write(manifestLine);
        _journal!.Write(journalLine);
        _pending++;

        if (_pending >= BatchFiles || Environment.TickCount64 - _lastCheckpoint >= MaxCheckpointMilliseconds)
            FlushCheckpoint();
    }

    internal void FlushCheckpoint()
    {
        if (_pending == 0)
            return;

        // Durability ordering is intentional. A journal entry is trusted only after
        // its manifest hash has already reached stable storage.
        _manifest!.Flush(flushToDisk: true);
        _journal!.Flush(flushToDisk: true);
        _pending = 0;
        _lastCheckpoint = Environment.TickCount64;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            FlushCheckpoint();
        }
        finally
        {
            _journal?.Dispose();
            _manifest?.Dispose();
            _disposed = true;
        }
    }

    private void EnsureOpen()
    {
        if (_manifest is not null)
            return;

        StateLayout.PrepareStateDirectory(_destinationRoot);
        _manifest = OpenAppend(StateLayout.ManifestPath(_destinationRoot), "manifest");
        try
        {
            _journal = OpenAppend(StateLayout.JournalPath(_destinationRoot), "state");
        }
        catch
        {
            _manifest.Dispose();
            _manifest = null;
            throw;
        }
    }

    private static FileStream OpenAppend(string path, string label)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new IOException($"Ruta inválida de {label}: {path}");
        Directory.CreateDirectory(parent);
        WindowsPath.EnsureNormalDirectory(parent, $"La carpeta de {label}");
        if (Directory.Exists(path) || (File.Exists(path) && WindowsPath.IsReparsePoint(path)))
            throw new IOException($"Entrada de estado no segura para {label}: {path}");

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
            BufferSize = 64 * 1024,
        });
    }
}

'''
s = s.replace(anchor, anchor + checkpoint, 1)
recovery.write_text(s, encoding='utf-8')

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')
old = '''    private static async Task WriterLoopAsync(
        DestinationWorker worker,
        CopyOptions options,
        CopyJob job,
        ConcurrentDictionary<string, byte[]> expectedHashes)
    {
        CurrentFile? current = null;
        try
'''
new = '''    private static async Task WriterLoopAsync(
        DestinationWorker worker,
        CopyOptions options,
        CopyJob job,
        ConcurrentDictionary<string, byte[]> expectedHashes)
    {
        CurrentFile? current = null;
        using var recovery = new RecoveryCheckpointWriter(worker.Root);
        try
'''
if old not in s:
    raise SystemExit('WriterLoop anchor not found')
s = s.replace(old, new, 1)

s = s.replace('''                        await FinishFileAsync(worker, current, end.Hash, options, job).ConfigureAwait(false);
''', '''                        await FinishFileAsync(worker, current, end.Hash, options, job, recovery).ConfigureAwait(false);
''', 1)

old = '''    private static async Task FinishFileAsync(
        DestinationWorker worker,
        CurrentFile current,
        byte[] expectedHash,
        CopyOptions options,
        CopyJob job)
'''
new = '''    private static async Task FinishFileAsync(
        DestinationWorker worker,
        CurrentFile current,
        byte[] expectedHash,
        CopyOptions options,
        CopyJob job,
        RecoveryCheckpointWriter recovery)
'''
if old not in s:
    raise SystemExit('FinishFile signature anchor not found')
s = s.replace(old, new, 1)

old = '''        AppendRecoveryState(worker.Root, current.Entry, expectedHash);
        worker.Progress.MarkDone();
'''
new = '''        recovery.Append(
            new RecoveryFile(
                current.Entry.SourcePath,
                current.Entry.RelativePath,
                current.Entry.Size,
                current.Entry.ModifiedUnixNanoseconds),
            expectedHash);
        worker.Progress.MarkDone();
'''
if old not in s:
    raise SystemExit('recovery append anchor not found')
s = s.replace(old, new, 1)

old = '''    private static void AppendRecoveryState(string root, FileEntry entry, byte[] hash) =>
        RecoveryManager.AppendDurable(
            root,
            new RecoveryFile(
                entry.SourcePath,
                entry.RelativePath,
                entry.Size,
                entry.ModifiedUnixNanoseconds),
            hash);

'''
if old not in s:
    raise SystemExit('old AppendRecoveryState helper not found')
s = s.replace(old, '')
engine.write_text(s, encoding='utf-8')

tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
s = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public async Task FanOutDeliversMultipleBlocksToEveryDestination()
'''
if anchor not in s:
    raise SystemExit('test insertion anchor not found')
new_test = r'''    [TestMethod]
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

'''
s = s.replace(anchor, new_test + anchor, 1)
tests.write_text(s, encoding='utf-8')
