$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = [IO.File]::ReadAllText($path)
$old = 'worker.DeviceScheduler.MaxOutstandingIo'
$new = 'worker.DeviceScheduler.ExplorationQueueDepth'
$count = ([regex]::Matches($text, [regex]::Escape($old))).Count
if ($count -ne 1) { throw "Expected exactly one $old occurrence, found $count" }
$text = $text.Replace($old, $new)
if ($text.Contains($old)) { throw 'Old fixed scheduler property remains in CopyEngine.' }
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $path
if (git diff --cached --quiet) { throw 'Adaptive QD CopyEngine migration produced no change.' }
git commit -m 'perf(core): feed write exploration depth from adaptive scheduler'
git push origin HEAD:main
