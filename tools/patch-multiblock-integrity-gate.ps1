$ErrorActionPreference = 'Stop'
$path = 'dotnet/RepartoCopier.Core/CopyEngine.cs'
$text = Get-Content -Raw $path
$old = '        if (current.Copied != current.Entry.Size)'
$new = '        if (current.ScheduledBytes != current.Entry.Size || current.Copied != current.Entry.Size)'
$count = ([regex]::Matches($text, [regex]::Escape($old))).Count
if ($count -ne 1) { throw "Expected one FinishFile size gate, found $count." }
$text = $text.Replace($old, $new)
if ($text.Contains($old)) { throw 'Old completion-only size gate remains.' }
Set-Content -Path $path -Value $text -NoNewline
