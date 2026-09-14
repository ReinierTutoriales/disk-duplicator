$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)

function Replace-Exact([string]$Old, [string]$New, [string]$Label) {
    if (-not $script:text.Contains($Old)) { throw "${Label}: exact pattern not found" }
    $script:text = $script:text.Replace($Old, $New)
}

Replace-Exact @'
    private const int PreallocationThreshold = 4 * 1024 * 1024;
    // Files at or below one writer chunk use Windows write-through instead of
    // paying for a separate FlushFileBuffers call after the write.
    private const int WriteThroughFileThreshold = 4 * 1024 * 1024;
'@ '' 'remove fixed preallocation/write-through thresholds'

Replace-Exact @'
                            current = BeginFile(worker, begin.Entry);
                            job.Telemetry.RecordFilePolicy(current.WriteThrough);
'@ @'
                            current = BeginFile(worker, begin.Entry);
'@ 'remove obsolete write policy telemetry call'

Replace-Exact @'
        var directRequested = DirectIoDestinationWriter.IsEligible(worker.Device, entry.Size);
        var writeThrough = !directRequested && entry.Size <= WriteThroughFileThreshold;
        var preallocationSize = StoragePreallocationPolicy.GetPreallocationSize(part, entry.Size, PreallocationThreshold);
        FileStream? stream = null;
        DirectIoDestinationWriter.Session? directSession = null;
        if (directRequested)
        {
            using (OpenPartStream(part, FileMode.CreateNew, offset: 0, writeThrough: false, preallocationSize)) { }
            if (!DirectIoDestinationWriter.TryOpen(part, worker.Device, entry.Size, out directSession))
                stream = ReopenPart(part, 0, writeThrough: false);
        }
        else
        {
            stream = OpenPartStream(part, FileMode.CreateNew, offset: 0, writeThrough, preallocationSize);
        }
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, directSession, writeThrough, directRequested);
'@ @'
        var directRequested = DirectIoDestinationWriter.IsEligible(worker.Device, entry.Size);
        var preallocationSize = StoragePreallocationPolicy.GetPreallocationSize(part, entry.Size);
        FileStream? stream = null;
        DirectIoDestinationWriter.Session? directSession = null;
        if (directRequested)
        {
            using (OpenPartStream(part, FileMode.CreateNew, offset: 0, preallocationSize)) { }
            if (!DirectIoDestinationWriter.TryOpen(part, worker.Device, entry.Size, out directSession))
                stream = ReopenPart(part, 0);
        }
        else
        {
            stream = OpenPartStream(part, FileMode.CreateNew, offset: 0, preallocationSize);
        }
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, directSession, directRequested);
'@ 'remove write-through from file open policy'

$text = $text.Replace('ReopenPart(current.PartPath, current.Copied, current.WriteThrough)', 'ReopenPart(current.PartPath, current.Copied)')
$text = $text.Replace('job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started), current.WriteThrough);', 'job.Telemetry.RecordWrite(data.Length, Stopwatch.GetElapsedTime(started));')

Replace-Exact @'
        else if (current.Stream is not null)
        {
            if (!current.WriteThrough)
            {
                var flushStarted = Stopwatch.GetTimestamp();
                current.Stream.Flush(flushToDisk: true);
                job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            }
            current.Stream.Dispose();
            current.Stream = null;
        }
'@ @'
        else if (current.Stream is not null)
        {
            var flushStarted = Stopwatch.GetTimestamp();
            current.Stream.Flush(flushToDisk: true);
            job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            current.Stream.Dispose();
            current.Stream = null;
        }
'@ 'make one final durable flush the only buffered durability barrier'

$text = $text.Replace('job.Telemetry.RecordCommit(Stopwatch.GetElapsedTime(commitStarted), current.WriteThrough);', 'job.Telemetry.RecordCommit(Stopwatch.GetElapsedTime(commitStarted));')
$text = $text.Replace('job.Telemetry.RecordRecovery(Stopwatch.GetElapsedTime(recoveryStarted), current.WriteThrough);', 'job.Telemetry.RecordRecovery(Stopwatch.GetElapsedTime(recoveryStarted));')

Replace-Exact @'
    private static FileStream ReopenPart(string path, long offset, bool writeThrough) =>
        OpenPartStream(path, FileMode.Open, offset, writeThrough, preallocationSize: 0);

    private static FileStream OpenPartStream(
        string path,
        FileMode mode,
        long offset,
        bool writeThrough,
        long preallocationSize)
    {
        var options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        if (writeThrough)
            options |= FileOptions.WriteThrough;
        var stream = new FileStream(path, new FileStreamOptions
'@ @'
    private static FileStream ReopenPart(string path, long offset) =>
        OpenPartStream(path, FileMode.Open, offset, preallocationSize: 0);

    private static FileStream OpenPartStream(
        string path,
        FileMode mode,
        long offset,
        long preallocationSize)
    {
        var options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        var stream = new FileStream(path, new FileStreamOptions
'@ 'remove FileOptions.WriteThrough from output stream'

Replace-Exact @'
        FileStream? stream,
        DirectIoDestinationWriter.Session? directSession,
        bool writeThrough,
        bool directRequested)
'@ @'
        FileStream? stream,
        DirectIoDestinationWriter.Session? directSession,
        bool directRequested)
'@ 'remove CurrentFile write-through constructor state'

Replace-Exact @'
        public DirectIoDestinationWriter.Session? DirectSession { get; set; } = directSession;
        public bool WriteThrough { get; } = writeThrough;
        public bool DirectRequested { get; } = directRequested;
'@ @'
        public DirectIoDestinationWriter.Session? DirectSession { get; set; } = directSession;
        public bool DirectRequested { get; } = directRequested;
'@ 'remove CurrentFile write-through property'

if ($text.Contains('WriteThroughFileThreshold')) { throw 'Write-through threshold remains.' }
if ($text.Contains('PreallocationThreshold')) { throw 'Preallocation threshold remains.' }
if ($text.Contains('current.WriteThrough')) { throw 'CurrentFile write-through consumer remains.' }
if ($text.Contains('FileOptions.WriteThrough')) { throw 'WriteThrough file option remains in CopyEngine.' }

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Write-through removal migration produced no changes.' }
git commit -m 'perf(core): remove per-write durability barrier'
git push origin HEAD:main
