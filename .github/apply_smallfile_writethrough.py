from pathlib import Path

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')

def one(old,new,label):
    global s
    c=s.count(old)
    if c!=1: raise SystemExit(f'{label}: expected 1 anchor, found {c}')
    s=s.replace(old,new,1)

one('''    private const int PreallocationThreshold = 4 * 1024 * 1024;''',
'''    private const int PreallocationThreshold = 4 * 1024 * 1024;
    // Files at or below one writer chunk use Windows write-through instead of
    // paying for a separate FlushFileBuffers call after the write.
    private const int WriteThroughFileThreshold = WriteChunkSize;''', 'threshold')

one('''        var stream = new FileStream(part, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
            PreallocationSize = entry.Size >= PreallocationThreshold ? entry.Size : 0,
        });
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream);''',
'''        var writeThrough = entry.Size <= WriteThroughFileThreshold;
        var stream = OpenPartStream(
            part,
            FileMode.CreateNew,
            offset: 0,
            writeThrough,
            entry.Size >= PreallocationThreshold ? entry.Size : 0);
        return new CurrentFile(entry, destination, part, transient.BackupPath, stream, writeThrough);''', 'begin stream')

one('''                current.Stream ??= ReopenPart(current.PartPath, current.Copied);''',
'''                current.Stream ??= ReopenPart(current.PartPath, current.Copied, current.WriteThrough);''', 'retry reopen')

one('''        if (current.Stream is not null)
        {
            // BufferSize=1 disables FileStream buffering; one durable flush is enough.
            var flushStarted = Stopwatch.GetTimestamp();
            current.Stream.Flush(flushToDisk: true);
            job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            current.Stream.Dispose();
            current.Stream = null;
        }''',
'''        if (current.Stream is not null)
        {
            if (!current.WriteThrough)
            {
                // Large files use cached sequential writes and one explicit durable flush.
                var flushStarted = Stopwatch.GetTimestamp();
                current.Stream.Flush(flushToDisk: true);
                job.Telemetry.RecordFlush(Stopwatch.GetElapsedTime(flushStarted));
            }
            // For small files FileOptions.WriteThrough already forces each write through
            // the Windows cache to the device, avoiding a second FlushFileBuffers round-trip.
            current.Stream.Dispose();
            current.Stream = null;
        }''', 'finish flush')

one('''    private static FileStream ReopenPart(string path, long offset)
    {
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1,
        });
        stream.Position = offset;
        return stream;
    }''',
'''    private static FileStream ReopenPart(string path, long offset, bool writeThrough) =>
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
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = options,
            BufferSize = 1,
            PreallocationSize = preallocationSize,
        });
        if (offset != 0)
            stream.Position = offset;
        return stream;
    }''', 'reopen helper')

one('''        string partPath,
        string backupPath,
        FileStream stream)''',
'''        string partPath,
        string backupPath,
        FileStream stream,
        bool writeThrough)''', 'current ctor')
one('''        public FileStream? Stream { get; set; } = stream;
        public long Copied { get; set; }''',
'''        public FileStream? Stream { get; set; } = stream;
        public bool WriteThrough { get; } = writeThrough;
        public long Copied { get; set; }''', 'current property')

engine.write_text(s,encoding='utf-8')

# Regression: exact data + verification on both sides of the threshold.
tests=Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t=tests.read_text(encoding='utf-8')
anchor='''    [TestMethod]\n    public async Task DiagnosticsSnapshotMeasuresCopyAndVerificationHotPaths()\n'''
if t.count(anchor)!=1: raise SystemExit('test anchor mismatch')
new=r'''    [TestMethod]
    public async Task WriteThroughSmallFilePathPreservesExactDataAndVerification()
    {
        using var temp = new TempDirectory("writethrough-small-files");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var small = new byte[4096];
        var medium = new byte[1024 * 1024];
        var boundary = new byte[4 * 1024 * 1024];
        var large = new byte[4 * 1024 * 1024 + 1];
        new Random(161803).NextBytes(small);
        new Random(161804).NextBytes(medium);
        new Random(161805).NextBytes(boundary);
        new Random(161806).NextBytes(large);
        await File.WriteAllBytesAsync(Path.Combine(source, "small.bin"), small);
        await File.WriteAllBytesAsync(Path.Combine(source, "medium.bin"), medium);
        await File.WriteAllBytesAsync(Path.Combine(source, "boundary.bin"), boundary);
        await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), large);
        var destinations = Enumerable.Range(0, 3)
            .Select(i => Directory.CreateDirectory(Path.Combine(temp.Path, $"d{i}")).FullName)
            .ToArray();
        await using var job = CopyEngine.Start(
            CopyPlan.Create(source, destinations, false, false),
            new CopyOptions(Verify: true, SkipSame: false, KeepGoing: false));
        await job.Completion.WaitAsync(TimeSpan.FromSeconds(90));
        AssertHealthy(job);
        foreach (var root in destinations)
        {
            var copied = Path.Combine(root, "Origen");
            CollectionAssert.AreEqual(small, await File.ReadAllBytesAsync(Path.Combine(copied, "small.bin")));
            CollectionAssert.AreEqual(medium, await File.ReadAllBytesAsync(Path.Combine(copied, "medium.bin")));
            CollectionAssert.AreEqual(boundary, await File.ReadAllBytesAsync(Path.Combine(copied, "boundary.bin")));
            CollectionAssert.AreEqual(large, await File.ReadAllBytesAsync(Path.Combine(copied, "large.bin")));
        }
        // Only the >4 MiB file should require an explicit FlushFileBuffers per destination.
        Assert.AreEqual(destinations.Length, job.DiagnosticsSnapshot().DurableFlushes);
    }

'''
t=t.replace(anchor,new+anchor,1)
tests.write_text(t,encoding='utf-8')

stress=Path('.github/writethrough-stress');stress.mkdir(parents=True,exist_ok=True)
(stress/'Stress.csproj').write_text(r'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="../../dotnet/RepartoCopier.Core/RepartoCopier.Core.csproj" /></ItemGroup></Project>''',encoding='utf-8')
(stress/'Program.cs').write_text(r'''using System.Diagnostics;using System.Text.Json;using RepartoCopier.Core;
static void H(CopyJob j){foreach(var s in j.Snapshot())if(s.Phase!=DestinationPhase.Done)throw new Exception($"{s.Phase}: {s.Error}");}
static async Task Run(string name,int files,int size){var root=Path.Combine(Path.GetTempPath(),"RC-WT",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);try{var src=Directory.CreateDirectory(Path.Combine(root,"src")).FullName;var b=new byte[size];var r=new Random(99);for(int i=0;i<files;i++){r.NextBytes(b);await File.WriteAllBytesAsync(Path.Combine(src,$"f{i:D4}.bin"),b);}var ds=Enumerable.Range(0,4).Select(i=>Directory.CreateDirectory(Path.Combine(root,$"d{i}")).FullName).ToArray();var sw=Stopwatch.StartNew();await using var j=CopyEngine.Start(CopyPlan.Create(src,ds,false,false),new CopyOptions(false,false,false));await j.Completion.WaitAsync(TimeSpan.FromMinutes(10));sw.Stop();H(j);Console.WriteLine($"WTSTRESS|{name}|wall_ms={sw.Elapsed.TotalMilliseconds:F0}|{JsonSerializer.Serialize(j.DiagnosticsSnapshot())}");}finally{try{Directory.Delete(root,true);}catch{}}}
await Run("medium-256x1MiB",256,1024*1024);
await Run("small-4096x4KiB",4096,4096);
''',encoding='utf-8')
