$ErrorActionPreference = 'Stop'
$patterns = 'TryReserveBacklog|ReserveBacklogAsync|RecordQueueWait|QueueWaitTime|_queueWaitTicks|deferred'
Get-ChildItem 'dotnet' -Recurse -Filter '*.cs' | ForEach-Object {
    Select-String -Path $_.FullName -Pattern $patterns | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath((Get-Location).Path, $_.Path)
        Write-Host "${relative}:$($_.LineNumber): $($_.Line.Trim())"
    }
}
