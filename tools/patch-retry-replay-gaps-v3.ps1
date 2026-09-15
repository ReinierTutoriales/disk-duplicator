$ErrorActionPreference = 'Stop'

& (Join-Path (Get-Location) 'tools/patch-retry-replay-gaps-v2.ps1')

$path = 'dotnet/RepartoCopier.Core.Tests/RetryAndReplayArchitectureTests.cs'
$text = [System.IO.File]::ReadAllText((Resolve-Path $path))
$old = @'
        var il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new AssertFailedException("WriteBlockAtOffsetAsync no expone IL.");
        var token = BitConverter.GetBytes(target.MetadataToken);
        var found = false;
        for (var i = 0; i <= il.Length - token.Length; i++)
        {
            if (il.AsSpan(i, token.Length).SequenceEqual(token))
            {
                found = true;
                break;
            }
        }
'@
$new = @'
        var stateMachine = method
            .GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()
            ?.StateMachineType
            ?? throw new AssertFailedException("WriteBlockAtOffsetAsync debe conservar su state machine async.");
        var moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new AssertFailedException("No se encontró MoveNext de WriteBlockAtOffsetAsync.");
        var il = moveNext.GetMethodBody()?.GetILAsByteArray()
            ?? throw new AssertFailedException("MoveNext de WriteBlockAtOffsetAsync no expone IL.");
        var token = BitConverter.GetBytes(target.MetadataToken);
        var found = false;
        for (var i = 0; i <= il.Length - token.Length; i++)
        {
            if (il.AsSpan(i, token.Length).SequenceEqual(token))
            {
                found = true;
                break;
            }
        }
'@
if (-not $text.Contains($old)) { throw 'Async retry contract anchor missing.' }
$text = $text.Replace($old, $new)
[System.IO.File]::WriteAllText((Resolve-Path $path), $text, [System.Text.UTF8Encoding]::new($false))

dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal
git diff --check
Write-Host 'Async production-consumer contract corrected.'
