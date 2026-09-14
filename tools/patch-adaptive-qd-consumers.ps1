$ErrorActionPreference = 'Stop'

$telemetry = 'dotnet/RepartoCopier.Core.Tests/ProgressTelemetryTests.cs'
$text = [IO.File]::ReadAllText($telemetry)
$old = 'Assert.IsTrue(schedulers[0].MaxOutstandingIo >= 1);'
$new = 'Assert.IsTrue(schedulers[0].CurrentQueueDepth >= 1);'
if ($text.Contains($old)) {
    $text = $text.Replace($old, $new)
    [IO.File]::WriteAllText($telemetry, $text, [Text.UTF8Encoding]::new($false))
}

$negativeContract = [IO.Path]::GetFullPath('dotnet/RepartoCopier.Core.Tests/AdaptiveQueueDepthArchitectureTests.cs')
$legacy = @()
Get-ChildItem 'dotnet' -Recurse -Filter '*.cs' | Where-Object {
    $_.FullName -ne $negativeContract
} | ForEach-Object {
    $matches = Select-String -Path $_.FullName -Pattern 'MaxOutstandingIo|RecommendedQueueDepth'
    foreach ($match in $matches) {
        $relative = [IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName)
        $legacy += "${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}
if ($legacy.Count -gt 0) {
    $legacy | ForEach-Object { Write-Host $_ }
    throw "Legacy fixed-QD symbols remain in dotnet/: $($legacy.Count) occurrence(s)."
}

git config user.name 'RepartoCopier CI Migration'
git config user.email '86568548+ReinierTutoriales@users.noreply.github.com'
git add -- $telemetry
if (git diff --cached --quiet) { throw 'No adaptive-QD consumer migration changes were produced.' }
git commit -m 'test(core): migrate scheduler telemetry to adaptive window'
git push origin HEAD:main
