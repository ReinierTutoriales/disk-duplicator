$ErrorActionPreference = 'Stop'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText((Resolve-Path $Path)) }
function Write-Text([string]$Path, [string]$Content) { [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, [System.Text.UTF8Encoding]::new($false)) }
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if (-not $text.Contains($Old)) { throw "Expected anchor missing in $Path`n$Old" }
    Write-Text $Path ($text.Replace($Old, $New))
}

$lease = @'
using System.Text;

namespace RepartoCopier.Core;

/// <summary>
/// Cross-process ownership lease for a destination's recovery namespace.
/// The lock file is intentionally persistent; exclusivity is provided by the
/// open handle with FileShare.None, so a crashed process releases the lease.
/// </summary>
internal sealed class DestinationStateLease : IDisposable
{
    private FileStream? _stream;

    private DestinationStateLease(FileStream stream, string destinationRoot)
    {
        _stream = stream;
        DestinationRoot = destinationRoot;
    }

    internal string DestinationRoot { get; }

    internal static DestinationStateLease Acquire(string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        StateLayout.PrepareStateDirectory(destinationRoot);
        var stateDirectory = StateLayout.StateDirectoryFor(destinationRoot);
        var lockPath = Path.Combine(stateDirectory, "active.lock");
        if (Directory.Exists(lockPath))
            throw new IOException($"El lock de estado no puede ser una carpeta: {lockPath}");
        if (File.Exists(lockPath))
            WindowsPath.EnsureRegularFile(lockPath, "El lock de estado");

        FileStream? stream = null;
        try
        {
            stream = new FileStream(lockPath, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.WriteThrough,
            });
            stream.SetLength(0);
            var owner = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId};utc={DateTimeOffset.UtcNow:O}");
            stream.Write(owner);
            stream.Flush(flushToDisk: true);
            return new DestinationStateLease(stream, Path.GetFullPath(destinationRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw new IOException(
                $"El destino ya está siendo usado por otra operación o no se pudo bloquear su estado: {destinationRoot}",
                ex);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'dotnet/RepartoCopier.Core/DestinationStateLease.cs'), $lease, [System.Text.UTF8Encoding]::new($false))

$engine = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
Replace-Exact $engine @'
        var prepared = Preflight(plan);
        var progress = prepared.DestinationRoots
            .Select(root => new DestinationProgress(root, prepared.TotalBytes, (ulong)prepared.Files.Count))
            .ToArray();
        var job = new CopyJob(progress);
        job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
        return job;
'@ @'
        var prepared = Preflight(plan);
        try
        {
            var progress = prepared.DestinationRoots
                .Select(root => new DestinationProgress(root, prepared.TotalBytes, (ulong)prepared.Files.Count))
                .ToArray();
            var job = new CopyJob(progress);
            job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
            return job;
        }
        catch
        {
            prepared.ReleaseStateLeases();
            throw;
        }
'@

Replace-Exact $engine @'
        var prepared = await Task.Run(() => Preflight(plan), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var progress = prepared.DestinationRoots
            .Select(root => new DestinationProgress(root, prepared.TotalBytes, (ulong)prepared.Files.Count))
            .ToArray();
        var job = new CopyJob(progress);
        job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
        return job;
'@ @'
        var prepared = await Task.Run(() => Preflight(plan), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = prepared.DestinationRoots
                .Select(root => new DestinationProgress(root, prepared.TotalBytes, (ulong)prepared.Files.Count))
                .ToArray();
            var job = new CopyJob(progress);
            job.Attach(Task.Run(() => RunAsync(prepared, progress, options, job), CancellationToken.None));
            return job;
        }
        catch
        {
            prepared.ReleaseStateLeases();
            throw;
        }
'@

Replace-Exact $engine @'
        var preverifiedSkips = CreateEmptySkipMasks(files.Count, destinationRoots.Length);
        for (var slot = 0; slot < destinationRoots.Length; slot++)
        {
            var root = destinationRoots[slot];
            PreflightSafety.ValidateDestinationLayout(root, directories, scan.Files);
            var completed = RecoveryManager.PrepareAndNormalize(sourceRoot, root, recoveryFiles);
            var skippedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                if (!completed.Contains(RecoveryManager.StateKey(recoveryFiles[fileIndex])))
                    continue;
                preverifiedSkips[fileIndex][slot] = true;
                skippedPaths.Add(files[fileIndex].RelativePath);
            }
            PreflightSafety.EnsureFreeSpace(root, scan.Files, skippedPaths);
            foreach (var relative in directories)
                EnsureDestinationDirectory(root, relative);
        }

        return new PreparedCopy(
            sourceRoot,
            destinationRoots,
            files,
            directories,
            totalBytes,
            preverifiedSkips,
            sourceIsDirectory ? scan : null,
            sourceDevice,
            destinationDevices);
'@ @'
        var preverifiedSkips = CreateEmptySkipMasks(files.Count, destinationRoots.Length);
        var stateLeases = new List<DestinationStateLease>(destinationRoots.Length);
        try
        {
            foreach (var root in destinationRoots)
                stateLeases.Add(DestinationStateLease.Acquire(root));

            for (var slot = 0; slot < destinationRoots.Length; slot++)
            {
                var root = destinationRoots[slot];
                PreflightSafety.ValidateDestinationLayout(root, directories, scan.Files);
                var completed = RecoveryManager.PrepareAndNormalize(sourceRoot, root, recoveryFiles);
                var skippedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
                {
                    if (!completed.Contains(RecoveryManager.StateKey(recoveryFiles[fileIndex])))
                        continue;
                    preverifiedSkips[fileIndex][slot] = true;
                    skippedPaths.Add(files[fileIndex].RelativePath);
                }
                PreflightSafety.EnsureFreeSpace(root, scan.Files, skippedPaths);
                foreach (var relative in directories)
                    EnsureDestinationDirectory(root, relative);
            }

            return new PreparedCopy(
                sourceRoot,
                destinationRoots,
                files,
                directories,
                totalBytes,
                preverifiedSkips,
                sourceIsDirectory ? scan : null,
                sourceDevice,
                destinationDevices,
                stateLeases.ToArray());
        }
        catch
        {
            foreach (var lease in stateLeases)
                lease.Dispose();
            throw;
        }
'@

Replace-Exact $engine @'
        finally
        {
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
                worker.Dispose();
            }
        }
'@ @'
        finally
        {
            foreach (var worker in workers)
            {
                worker.Channel.Writer.TryComplete();
                DrainAndRelease(worker);
                worker.Dispose();
            }
            copy.ReleaseStateLeases();
        }
'@

Replace-Exact $engine @'
    private sealed record PreparedCopy(
        string SourceRoot,
        string[] DestinationRoots,
        IReadOnlyList<FileEntry> Files,
        IReadOnlyList<string> Directories,
        ulong TotalBytes,
        bool[][] PreverifiedSkips,
        SourceTreeScan? SourceScan,
        StorageDeviceInfo SourceDevice,
        StorageDeviceInfo[] DestinationDevices);
'@ @'
    private sealed record PreparedCopy(
        string SourceRoot,
        string[] DestinationRoots,
        IReadOnlyList<FileEntry> Files,
        IReadOnlyList<string> Directories,
        ulong TotalBytes,
        bool[][] PreverifiedSkips,
        SourceTreeScan? SourceScan,
        StorageDeviceInfo SourceDevice,
        StorageDeviceInfo[] DestinationDevices,
        DestinationStateLease[] StateLeases)
    {
        internal void ReleaseStateLeases()
        {
            foreach (var lease in StateLeases)
                lease.Dispose();
        }
    }
'@

$tests = 'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs'
Replace-Exact $tests @'
    [TestMethod]
    public async Task PipelineGovernorCancellationDoesNotLeakPrefetchCapacity()
'@ @'
    [TestMethod]
    public void DestinationStateLeaseRejectsConcurrentOwnerAndRecoversAfterRelease()
    {
        using var temp = new TempDirectory("destination-state-lease");
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;

        using (var first = DestinationStateLease.Acquire(destination))
        {
            var error = Assert.ThrowsExactly<IOException>(() => DestinationStateLease.Acquire(destination));
            StringAssert.Contains(error.Message, "ya está siendo usado");
        }

        using var reacquired = DestinationStateLease.Acquire(destination);
        Assert.AreEqual(Path.GetFullPath(destination), reacquired.DestinationRoot);
    }

    [TestMethod]
    public async Task PipelineGovernorCancellationDoesNotLeakPrefetchCapacity()
'@

$contract = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
Replace-Exact $contract @'
    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@ @'
    [TestMethod]
    public void DestinationStateLeaseIsAcquiredByPreflightAndReleasedByRunLifetime()
    {
        var acquire = typeof(DestinationStateLease).GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("DestinationStateLease.Acquire no existe.");
        var release = typeof(CopyEngine).GetNestedType("PreparedCopy", BindingFlags.NonPublic)
            ?.GetMethod("ReleaseStateLeases", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new AssertFailedException("PreparedCopy.ReleaseStateLeases no existe.");
        var preflight = typeof(CopyEngine).GetMethod("Preflight", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("CopyEngine.Preflight no existe.");
        Assert.IsTrue(MethodCalls(preflight, acquire), "Preflight debe adquirir el lease antes de recovery.");

        var run = typeof(CopyEngine).GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("CopyEngine.RunAsync no existe.");
        var stateMachine = run.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new AssertFailedException("RunAsync debe conservar su state machine async.");
        var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("No se encontró MoveNext de RunAsync.");
        Assert.IsTrue(MethodCalls(moveNext, release), "RunAsync debe liberar los leases en su finally productivo.");
    }

    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@

Write-Host 'Destination state lease patch applied.'
