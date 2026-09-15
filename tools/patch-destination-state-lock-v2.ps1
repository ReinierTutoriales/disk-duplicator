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
/// The lock lives in the common state container rather than inside the current
/// state id so acquiring it cannot create the current state directory and skip
/// legacy state migration.
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
        var normalizedRoot = Path.GetFullPath(destinationRoot);
        var stateDirectory = StateLayout.StateDirectoryFor(normalizedRoot);
        var container = Path.GetDirectoryName(stateDirectory)
            ?? throw new IOException("La ruta del estado no tiene contenedor.");
        Directory.CreateDirectory(container);
        WindowsPath.EnsureNormalDirectory(container, "El contenedor de estado");
        var lockDirectory = Path.Combine(container, ".locks");
        Directory.CreateDirectory(lockDirectory);
        WindowsPath.EnsureNormalDirectory(lockDirectory, "El directorio de locks de estado");

        var lockPath = Path.Combine(lockDirectory, $"{StateLayout.StateId(normalizedRoot)}.lock");
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
            return new DestinationStateLease(stream, normalizedRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw new IOException(
                $"El destino ya está siendo usado por otra operación o no se pudo bloquear su estado: {normalizedRoot}",
                ex);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
'@
Write-Text 'dotnet/RepartoCopier.Core/DestinationStateLease.cs' $lease

$tests = 'dotnet/RepartoCopier.Core.Tests/CoreParityTests.cs'
Replace-Exact $tests @'
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
'@ @'
    [TestMethod]
    public void DestinationStateLeaseRejectsConcurrentOwnerAndRecoversAfterRelease()
    {
        using var temp = new TempDirectory("destination-state-lease");
        var destination = Directory.CreateDirectory(Path.Combine(temp.Path, "dest")).FullName;
        var currentState = StateLayout.StateDirectoryFor(destination);

        using (var first = DestinationStateLease.Acquire(destination))
        {
            Assert.IsFalse(
                Directory.Exists(currentState),
                "Acquire no debe crear el state id actual antes de que RecoveryManager pueda migrar estado legacy.");
            var error = Assert.ThrowsExactly<IOException>(() => DestinationStateLease.Acquire(destination));
            StringAssert.Contains(error.Message, "ya está siendo usado");
        }

        using var reacquired = DestinationStateLease.Acquire(destination);
        Assert.AreEqual(Path.GetFullPath(destination), reacquired.DestinationRoot);
        Assert.IsFalse(Directory.Exists(currentState));
    }
'@

Write-Host 'Destination state lease placement corrected.'
