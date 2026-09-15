$ErrorActionPreference = 'Stop'

& (Join-Path (Get-Location) 'tools/patch-retry-replay-gaps.ps1')

$path = 'dotnet/RepartoCopier.Core.Tests/RetryAndReplayArchitectureTests.cs'
$text = [System.IO.File]::ReadAllText((Resolve-Path $path))
if (-not $text.Contains('using System.Reflection;')) { throw 'Retry/replay test using anchor missing.' }
$text = $text.Replace('using System.Reflection;', "using System.Diagnostics;`r`nusing System.Reflection;")
$text = $text.Replace('[DataTestMethod]', '[TestMethod]')
[System.IO.File]::WriteAllText((Resolve-Path $path), $text, [System.Text.UTF8Encoding]::new($false))

dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal
git diff --check
Write-Host 'Retry/replay test compatibility corrected.'
