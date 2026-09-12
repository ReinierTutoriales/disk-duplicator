from pathlib import Path

preflight = Path('dotnet/RepartoCopier.Core/PreflightSafety.cs')
p = preflight.read_text(encoding='utf-8')
anchor = '''    internal static void ValidateDestinationLayout(
'''
if anchor not in p:
    raise SystemExit('Preflight insertion anchor not found')
method = r'''    internal static void ValidateSourceTreeSnapshot(string sourceRoot, SourceTreeScan expected)
    {
        WindowsPath.EnsureNormalDirectory(sourceRoot, "El origen");
        var current = ScanDirectory(sourceRoot);

        if (current.Directories.Count != expected.Directories.Count ||
            current.Files.Count != expected.Files.Count)
        {
            throw new IOException("La estructura del origen cambió durante la copia.");
        }

        for (var index = 0; index < expected.Directories.Count; index++)
        {
            if (!string.Equals(
                    expected.Directories[index],
                    current.Directories[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("La estructura de carpetas del origen cambió durante la copia.");
            }
        }

        for (var index = 0; index < expected.Files.Count; index++)
        {
            var before = expected.Files[index];
            var after = current.Files[index];
            if (!string.Equals(before.RelativePath, after.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                before.Size != after.Size ||
                before.LastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                throw new IOException($"El árbol de archivos del origen cambió durante la copia: {before.RelativePath}.");
            }
        }
    }

'''
p = p.replace(anchor, method + anchor, 1)
preflight.write_text(p, encoding='utf-8')

engine = Path('dotnet/RepartoCopier.Core/CopyEngine.cs')
s = engine.read_text(encoding='utf-8')
# Carry the immutable directory scan into the prepared copy; single-file jobs keep null.
s = s.replace(
'''            directories,
            totalBytes,
            preverifiedSkips);''',
'''            directories,
            totalBytes,
            preverifiedSkips,
            sourceIsDirectory ? scan : null);''', 1)

# Verify the selected directory still contains exactly the preflight tree once all source reads are complete.
loop_end = '''                await DeliverAsync(active.Where(worker => worker.IsActive).ToArray(), new EndMessage(hash), countsData: false, job).ConfigureAwait(false);
            }
        }
        finally
'''
loop_replacement = '''                await DeliverAsync(active.Where(worker => worker.IsActive).ToArray(), new EndMessage(hash), countsData: false, job).ConfigureAwait(false);
            }

            if (copy.SourceScan is not null)
                PreflightSafety.ValidateSourceTreeSnapshot(copy.SourceRoot, copy.SourceScan);
        }
        finally
'''
if loop_end not in s:
    raise SystemExit('producer final validation anchor not found')
s = s.replace(loop_end, loop_replacement, 1)

record_old = '''        IReadOnlyList<string> Directories,
        ulong TotalBytes,
        bool[][] PreverifiedSkips);'''
record_new = '''        IReadOnlyList<string> Directories,
        ulong TotalBytes,
        bool[][] PreverifiedSkips,
        SourceTreeScan? SourceScan);'''
if record_old not in s:
    raise SystemExit('PreparedCopy anchor not found')
s = s.replace(record_old, record_new, 1)
engine.write_text(s, encoding='utf-8')

# Deterministic regression for additions, removals and metadata changes in the source tree.
tests = Path('dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs')
t = tests.read_text(encoding='utf-8')
anchor = '''    [TestMethod]
    public void PreflightSpaceMathMatchesRustRules()
'''
if anchor not in t:
    raise SystemExit('source tree test anchor not found')
new_tests = '''    [TestMethod]
    public void SourceTreeSnapshotDetectsStructuralAndMetadataMutation()
    {
        using var temp = new TempDirectory("source-tree-snapshot");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "Origen")).FullName;
        var file = Path.Combine(source, "a.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var baseline = PreflightSafety.ScanDirectory(source);

        File.WriteAllText(Path.Combine(source, "added.txt"), "new");
        Assert.ThrowsExactly<IOException>(() =>
            PreflightSafety.ValidateSourceTreeSnapshot(source, baseline));
        File.Delete(Path.Combine(source, "added.txt"));

        File.WriteAllBytes(file, [1, 2, 3, 4]);
        Assert.ThrowsExactly<IOException>(() =>
            PreflightSafety.ValidateSourceTreeSnapshot(source, baseline));
    }

'''
t = t.replace(anchor, new_tests + anchor, 1)
tests.write_text(t, encoding='utf-8')
