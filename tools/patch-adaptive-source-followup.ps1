$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)
$old = @'
            var additional = Math.Max(0L, available - reserve);
            return additional >= long.MaxValue - usedBytes ? long.MaxValue : usedBytes + additional;
'@
$new = @'
            var additional = Math.Max(0L, available - reserve);
            var safe = additional >= long.MaxValue - usedBytes ? long.MaxValue : usedBytes + additional;
            // Even under memory pressure the engine must be able to make forward progress
            // with one source block; this is a floor, never an upper throughput ceiling.
            return Math.Max((long)BlockSize, safe);
'@
if (-not $text.Contains($old)) { throw 'patrón de capacidad segura no encontrado' }
$text = $text.Replace($old, $new)
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'El follow-up adaptive source no produjo cambios.' }
git commit -m 'fix(core): guarantee one-block source progress under pressure'
git push origin HEAD:main
