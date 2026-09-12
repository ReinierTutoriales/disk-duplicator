from pathlib import Path

# 1) Stream directory enumeration instead of materializing every directory into an array.
preflight = Path('dotnet/RepartoCopier.Core/PreflightSafety.cs')
p = preflight.read_text(encoding='utf-8')
old_enum = '''            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex)
            {
                throw new IOException($"No se pudo enumerar {directory}: {ex.Message}", ex);
            }

            foreach (var entry in entries)
'''
new_enum = '''            foreach (var entry in EnumerateDirectoryEntries(directory))
'''
if old_enum not in p:
    raise SystemExit('directory enumeration anchor not found')
p = p.replace(old_enum, new_enum, 1)
anchor = '''    internal static void ValidateSourceTreeSnapshot(string sourceRoot, SourceTreeScan expected)
'''
helper = r'''    private static IEnumerable<string> EnumerateDirectoryEntries(string directory)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        }
        catch (Exception ex)
        {
            throw new IOException($"No se pudo enumerar {directory}: {ex.Message}", ex);
        }

        using (enumerator)
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch (Exception ex)
                {
                    throw new IOException($"No se pudo enumerar {directory}: {ex.Message}", ex);
                }
                if (!moved)
                    yield break;
                yield return enumerator.Current;
            }
        }
    }

'''
if anchor not in p:
    raise SystemExit('enumeration helper anchor not found')
p = p.replace(anchor, helper + anchor, 1)
preflight.write_text(p, encoding='utf-8')

# 2) Recovery verification must not allocate a new 4 MiB LOH buffer (plus FileStream buffering) per file.
recovery = Path('dotnet/RepartoCopier.Core/Recovery.cs')
r = recovery.read_text(encoding='utf-8')
if not r.startswith('using System.Buffers;'):
    r = 'using System.Buffers;\n' + r
old_hash = '''    private static byte[] HashFile(string path)
    {
        WindowsPath.EnsureRegularFile(path, "El archivo para verificación");
        using var hasher = Hasher.New();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[4 * 1024 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            hasher.Update(buffer.AsSpan(0, read));
        }
        return hasher.Finalize().AsSpan().ToArray();
    }
'''
new_hash = '''    private static byte[] HashFile(string path)
    {
        WindowsPath.EnsureRegularFile(path, "El archivo para verificación");
        using var hasher = Hasher.New();
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
            BufferSize = 1,
        });
        const int bufferSize = 4 * 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, bufferSize);
                if (read == 0)
                    break;
                hasher.Update(buffer.AsSpan(0, read));
            }
            return hasher.Finalize().AsSpan().ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
'''
if old_hash not in r:
    raise SystemExit('recovery hash anchor not found')
r = r.replace(old_hash, new_hash, 1)
recovery.write_text(r, encoding='utf-8')

# 3) Compute the transient BLAKE3 identifier once per destination file and reuse it for part+backup.
storage = Path('dotnet/RepartoCopier.Core/Storage.cs')
st = storage.read_text(encoding='utf-8')
anchor = '''    public static string PartPath(string destinationRoot, string destinationFile) =>
'''
paths_method = r'''    public static (string PartPath, string BackupPath) TransientPaths(
        string destinationRoot,
        string destinationFile)
    {
        var temp = Path.Combine(StateDirectoryFor(destinationRoot), "tmp");
        var id = TransientId(destinationFile);
        return (
            Path.Combine(temp, $"{id}.part"),
            Path.Combine(temp, $"{id}.bak"));
    }

'''
if anchor not in st:
    raise SystemExit('StateLayout path anchor not found')
st = st.replace(anchor, paths_method + anchor, 1)
storage.write_text(st, encoding='utf-8')

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')
s = s.replace(
'''        var part = StateLayout.PartPath(worker.Root, destination);
        TryDelete(part);
''',
'''        var transient = StateLayout.TransientPaths(worker.Root, destination);
        var part = transient.PartPath;
        TryDelete(part);
''', 1)
s = s.replace(
'''        return new CurrentFile(entry, destination, part, stream);''',
'''        return new CurrentFile(entry, destination, part, transient.BackupPath, stream);''', 1)
s = s.replace(
'''        CommitPart(worker.Root, current.PartPath, current.DestinationPath);''',
'''        CommitPart(current.PartPath, current.DestinationPath, current.BackupPath);''', 1)
s = s.replace(
'''    private static void CommitPart(string destinationRoot, string part, string destination)
''',
'''    private static void CommitPart(string part, string destination, string backup)
''', 1)
s = s.replace(
'''        var backup = StateLayout.BackupPath(destinationRoot, destination);
        TryDelete(backup);
''',
'''        TryDelete(backup);
''', 1)
s = s.replace(
'''        string partPath,
        FileStream stream)
''',
'''        string partPath,
        string backupPath,
        FileStream stream)
''', 1)
s = s.replace(
'''        public string PartPath { get; } = partPath;
        public FileStream? Stream { get; set; } = stream;
''',
'''        public string PartPath { get; } = partPath;
        public string BackupPath { get; } = backupPath;
        public FileStream? Stream { get; set; } = stream;
''', 1)
engine.write_text(s, encoding='utf-8')

# Stress dense metadata enumeration and ensure the optimized transient path derivation stays identical.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public void SourceTreeSnapshotDetectsStructuralAndMetadataMutation()
'''
new_tests = r'''    [TestMethod]
    public void DenseDirectoryScanStreamsThousandsOfEntriesExactly()
    {
        using var temp = new TempDirectory("dense-streaming-scan");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        const int fileCount = 3000;
        for (var index = 0; index < fileCount; index++)
            File.WriteAllText(Path.Combine(source, $"file-{index:D4}.txt"), index.ToString());

        var scan = PreflightSafety.ScanDirectory(source);
        Assert.AreEqual(fileCount, scan.Files.Count);
        Assert.AreEqual(0, scan.Directories.Count);
        Assert.AreEqual("file-0000.txt", scan.Files[0].RelativePath);
        Assert.AreEqual($"file-{fileCount - 1:D4}.txt", scan.Files[^1].RelativePath);
    }

    [TestMethod]
    public void TransientPathsReuseOneIdentityWithoutChangingLayout()
    {
        using var temp = new TempDirectory("transient-paths");
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var destination = Path.Combine(root, "nested", "payload.bin");
        var pair = StateLayout.TransientPaths(root, destination);
        Assert.AreEqual(StateLayout.PartPath(root, destination), pair.PartPath);
        Assert.AreEqual(StateLayout.BackupPath(root, destination), pair.BackupPath);
    }

'''
if anchor not in t:
    raise SystemExit('hotpath test anchor not found')
t = t.replace(anchor, new_tests + anchor, 1)
tests.write_text(t, encoding='utf-8')
