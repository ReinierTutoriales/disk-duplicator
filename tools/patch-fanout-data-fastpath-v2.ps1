$ErrorActionPreference = 'Stop'

try {
    & ./tools/patch-fanout-data-fastpath.ps1
    throw 'Base fast-path migrator unexpectedly completed; wrapper is no longer needed.'
}
catch {
    if ($_.Exception.Message -notlike '*Legacy per-destination data delivery route remains: 1 occurrence*') {
        throw
    }
}

$contract = [IO.Path]::GetFullPath('dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs')
$legacy = @()
Get-ChildItem 'dotnet' -Recurse -Filter '*.cs' | Where-Object { $_.FullName -ne $contract } | ForEach-Object {
    $matches = Select-String -Path $_.FullName -Pattern 'backlogReserved|DeliverOneAsync'
    foreach ($match in $matches) {
        $relative = [IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName)
        $legacy += "${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}
if ($legacy.Count -gt 0) {
    $legacy | ForEach-Object { Write-Host $_ }
    throw "Legacy per-destination data delivery route remains outside negative contract: $($legacy.Count) occurrence(s)."
}

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- dotnet/RepartoCopier.Core/CopyEngine.cs dotnet/RepartoCopier.Core.Tests/UnificationContractTests.cs
if (git diff --cached --quiet) { throw 'FAN-OUT data fast-path wrapper found no staged migration.' }
git commit -m 'perf(core): specialize FAN-OUT data delivery fast path'
git push origin HEAD:main
