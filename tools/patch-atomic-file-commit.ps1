$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)

$oldCall = '        CommitPart(current.PartPath, current.DestinationPath, current.BackupPath);'
$newCall = '        AtomicFileCommit.Commit(current.PartPath, current.DestinationPath, current.BackupPath);'
if (([regex]::Matches($text, [regex]::Escape($oldCall))).Count -ne 1) {
    throw 'Expected exactly one CommitPart production call.'
}
$text = $text.Replace($oldCall, $newCall)

$oldMethod = @'
    private static void CommitPart(string part, string destination, string backup)
    {
        if (!File.Exists(destination))
        {
            File.Move(part, destination);
            return;
        }

        WindowsPath.EnsureRegularFile(destination, "El archivo de destino");
        TryDelete(backup);
        File.Move(destination, backup);
        try
        {
            File.Move(part, destination);
            TryDelete(backup);
        }
        catch (Exception commitError)
        {
            try
            {
                File.Move(backup, destination);
            }
            catch (Exception restoreError)
            {
                throw new IOException(
                    $"CRÍTICO: falló el reemplazo de {destination} ({commitError.Message}) y también restaurar {backup} ({restoreError.Message}). El backup permanece en {backup}.",
                    new AggregateException(commitError, restoreError));
            }
            throw new IOException($"No se pudo reemplazar {destination}; el original fue restaurado.", commitError);
        }
    }

'@
if (-not $text.Contains($oldMethod)) { throw 'Legacy CommitPart method exact block not found.' }
$text = $text.Replace($oldMethod, '')
if ($text.Contains('CommitPart(')) { throw 'Legacy CommitPart route remains.' }
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

$legacy = Select-String -Path $path -Pattern 'File\.Move\(destination, backup\)'
if ($legacy) { throw 'Legacy manual replacement dance remains in CopyEngine.' }

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Atomic commit migration produced no changes.' }
git commit -m 'perf(core): route commits through atomic file replacement'
git push origin HEAD:main
