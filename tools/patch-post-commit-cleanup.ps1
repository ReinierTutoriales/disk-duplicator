$ErrorActionPreference = 'Stop'

function Read-Text([string]$Path) { [System.IO.File]::ReadAllText((Resolve-Path $Path)) }
function Write-Text([string]$Path, [string]$Content) { [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, [System.Text.UTF8Encoding]::new($false)) }
function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if (-not $text.Contains($Old)) { throw "Expected anchor missing in $Path`n$Old" }
    Write-Text $Path ($text.Replace($Old, $New))
}

$postCleanup = @'
namespace RepartoCopier.Core;

/// <summary>
/// Cleanup that happens only after the namespace commit is already valid.
/// A stale backup is recoverable housekeeping and must not turn a successful
/// replacement into a reported copy failure.
/// </summary>
internal static class PostCommitCleanup
{
    internal static bool TryDeleteRegularFile(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        try
        {
            if (!File.Exists(path))
                return !Directory.Exists(path);
            WindowsPath.EnsureRegularFile(path, label);
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'dotnet/RepartoCopier.Core/PostCommitCleanup.cs'), $postCleanup, [System.Text.UTF8Encoding]::new($false))

$atomic = @'
namespace RepartoCopier.Core;

/// <summary>
/// Commits a durable temporary file into its final destination using the smallest
/// available namespace operation. Existing files use the operating system's
/// replace primitive instead of a manual destination->backup->destination dance.
/// </summary>
internal static class AtomicFileCommit
{
    internal static void Commit(string part, string destination, string backup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(backup);

        if (!File.Exists(destination))
        {
            File.Move(part, destination);
            return;
        }

        WindowsPath.EnsureRegularFile(destination, "El archivo de destino");
        DeleteBackupIfPresent(backup);

        try
        {
            File.Replace(part, destination, backup, ignoreMetadataErrors: false);
        }
        catch (Exception commitError)
        {
            // ReplaceFile normally keeps the replaced path valid on failure. If a
            // filesystem edge case leaves only the generated backup, restore it.
            if (!File.Exists(destination) && File.Exists(backup))
            {
                try
                {
                    File.Move(backup, destination);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"CRÍTICO: falló el reemplazo de {destination} y también restaurar {backup}.",
                        new AggregateException(commitError, restoreError));
                }
            }

            throw new IOException(
                File.Exists(backup)
                    ? $"No se pudo reemplazar {destination}; el backup permanece en {backup}."
                    : $"No se pudo reemplazar {destination}.",
                commitError);
        }

        PostCommitCleanup.TryDeleteRegularFile(backup, "El backup de reemplazo");
    }

    private static void DeleteBackupIfPresent(string backup)
    {
        if (Directory.Exists(backup))
            throw new IOException($"El backup de reemplazo no puede ser una carpeta: {backup}");
        if (!File.Exists(backup))
            return;
        WindowsPath.EnsureRegularFile(backup, "El backup de reemplazo");
        File.Delete(backup);
    }
}
'@
Write-Text 'dotnet/RepartoCopier.Core/AtomicFileCommit.cs' $atomic

$recovery = 'dotnet/RepartoCopier.Core/Recovery.cs'
Replace-Exact $recovery @'
        try
        {
            File.Move(tmp, path);
            if (hadOld)
                File.Delete(backup);
        }
        catch (Exception commitError)
        {
            if (hadOld && File.Exists(backup))
            {
                try
                {
                    File.Move(backup, path);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"CRÍTICO: falló el commit del journal {path} y también su restauración desde {backup}.",
                        new AggregateException(commitError, restoreError));
                }
            }
            throw new IOException($"No se pudo actualizar el journal {path}.", commitError);
        }
'@ @'
        try
        {
            File.Move(tmp, path);
        }
        catch (Exception commitError)
        {
            if (hadOld && File.Exists(backup) && !File.Exists(path))
            {
                try
                {
                    File.Move(backup, path);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"CRÍTICO: falló el commit del journal {path} y también su restauración desde {backup}.",
                        new AggregateException(commitError, restoreError));
                }
            }
            throw new IOException($"No se pudo actualizar el journal {path}.", commitError);
        }

        if (hadOld)
            PostCommitCleanup.TryDeleteRegularFile(backup, "backup de estado");
'@

Replace-Exact $recovery @'
        File.Move(path, backup);
        try
        {
            File.Move(tmp, path);
            File.Delete(backup);
        }
        catch (Exception commitError)
        {
            try
            {
                File.Move(backup, path);
            }
            catch (Exception restoreError)
            {
                throw new IOException(
                    $"CRÍTICO: falló la compactación de {path} y su restauración.",
                    new AggregateException(commitError, restoreError));
            }
            throw new IOException($"No se pudo compactar {path}; el manifest anterior fue restaurado.", commitError);
        }
'@ @'
        File.Move(path, backup);
        try
        {
            File.Move(tmp, path);
        }
        catch (Exception commitError)
        {
            if (!File.Exists(path))
            {
                try
                {
                    File.Move(backup, path);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        $"CRÍTICO: falló la compactación de {path} y su restauración.",
                        new AggregateException(commitError, restoreError));
                }
            }
            throw new IOException($"No se pudo compactar {path}; el manifest anterior permanece recuperable.", commitError);
        }

        PostCommitCleanup.TryDeleteRegularFile(backup, "backup de manifest");
'@

$atomicTests = 'dotnet/RepartoCopier.Core.Tests/AtomicFileCommitTests.cs'
Replace-Exact $atomicTests @'
    private sealed class TempScope : IDisposable
'@ @'
    [TestMethod]
    public void PostCommitCleanupDoesNotFailAValidCommitWhenBackupIsTemporarilyLocked()
    {
        using var temp = new TempScope();
        var backup = Path.Combine(temp.Path, "locked.backup");
        File.WriteAllBytes(backup, [7, 8, 9]);

        using (var held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.IsFalse(PostCommitCleanup.TryDeleteRegularFile(backup, "backup bloqueado"));
            Assert.IsTrue(File.Exists(backup));
        }

        Assert.IsTrue(PostCommitCleanup.TryDeleteRegularFile(backup, "backup liberado"));
        Assert.IsFalse(File.Exists(backup));
    }

    private sealed class TempScope : IDisposable
'@

$contract = 'dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs'
Replace-Exact $contract @'
    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@ @'
    [TestMethod]
    public void DataAndRecoveryCommitsUsePostCommitCleanupWithoutOwningCommitSuccess()
    {
        var target = typeof(PostCommitCleanup).GetMethod(
            "TryDeleteRegularFile",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("PostCommitCleanup.TryDeleteRegularFile no existe.");
        var callers = new[]
        {
            typeof(AtomicFileCommit).GetMethod("Commit", BindingFlags.Static | BindingFlags.NonPublic),
            typeof(RecoveryManager).GetMethod("RewriteCompleted", BindingFlags.Static | BindingFlags.NonPublic),
            typeof(RecoveryManager).GetMethod("CompactManifest", BindingFlags.Static | BindingFlags.NonPublic),
        };

        foreach (var caller in callers)
        {
            Assert.IsNotNull(caller);
            Assert.IsTrue(MethodCalls(caller!, target), $"{caller!.Name} debe consumir PostCommitCleanup.TryDeleteRegularFile.");
        }
    }

    [TestMethod]
    public void DestinationWriterKeepsMultipleBlocksInFlightWithExplicitOffsets()
'@

Replace-Exact $contract @'
}
'@ @'
    private static bool MethodCalls(MethodInfo caller, MethodInfo target)
    {
        var il = caller.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return false;
        var token = BitConverter.GetBytes(target.MetadataToken);
        for (var index = 0; index <= il.Length - token.Length; index++)
        {
            if (il.AsSpan(index, token.Length).SequenceEqual(token))
                return true;
        }
        return false;
    }
}
'@

Write-Host 'Post-commit cleanup patch applied.'
