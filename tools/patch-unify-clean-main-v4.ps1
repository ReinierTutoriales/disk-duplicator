$ErrorActionPreference = 'Stop'

$source = 'tools/patch-unify-clean-main-v2.ps1'
$temp = 'tools/.patch-unify-clean-main-runtime.ps1'
$content = [System.IO.File]::ReadAllText((Resolve-Path $source))
$content = [regex]::Replace($content, '(?m)^\$attr = .*\r?\n', '')
$marker = "Write-Host '=== Forbidden legacy references after cleanup ==='"
$cut = $content.IndexOf($marker)
if ($cut -lt 0) { throw 'Legacy-gate marker missing from base migrator.' }
$content = $content.Substring(0, $cut) + "Write-Host 'Base cleanup stage applied.'`r`n"
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $temp), $content, [System.Text.UTF8Encoding]::new($false))
try {
    & (Join-Path (Get-Location) $temp)
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}

# Move every real test/consumer to the canonical CRC32C diagnostics names.
Get-ChildItem 'dotnet/RepartoCopier.Core.Tests' -Recurse -Filter '*.cs' |
    Where-Object { $_.Name -ne 'UnificationContractTests.cs' } |
    ForEach-Object {
        $text = [System.IO.File]::ReadAllText($_.FullName)
        $next = $text.Replace('VerifyHashBytesPerSecond', 'VerifyCrc32CBytesPerSecond')
        $next = $next.Replace('VerifyHashBytes', 'VerifyCrc32CBytes')
        $next = $next.Replace('VerifyHashTime', 'VerifyCrc32CTime')
        $next = $next.Replace('RecordVerifyHash', 'RecordVerifyCrc32C')
        if ($next -ne $text) {
            [System.IO.File]::WriteAllText($_.FullName, $next, [System.Text.UTF8Encoding]::new($false))
        }
    }

# Remove compatibility-only assertions that became tautologies after canonicalization.
$diagPath = 'dotnet/RepartoCopier.Core.Tests/VerificationDiagnosticsTests.cs'
$diag = [System.IO.File]::ReadAllText((Resolve-Path $diagPath))
$diag = [regex]::Replace($diag, '(?m)^\s*Assert\.AreEqual\(snapshot\.VerifyCrc32CBytes, snapshot\.VerifyCrc32CBytes\);\r?\n', '')
$diag = [regex]::Replace($diag, '(?m)^\s*Assert\.AreEqual\(snapshot\.VerifyCrc32CTime, snapshot\.VerifyCrc32CTime\);\r?\n', '')
$diag = [regex]::Replace($diag, '(?m)^\s*Assert\.AreEqual\(snapshot\.VerifyCrc32CBytesPerSecond, snapshot\.VerifyCrc32CBytesPerSecond\);\r?\n', '')
[System.IO.File]::WriteAllText((Resolve-Path $diagPath), $diag, [System.Text.UTF8Encoding]::new($false))

# Keep docs current while allowing historical/negative contract names where they are explanatory.
$roadmapPath = 'docs/FANOUT-PERFORMANCE-ROADMAP.md'
$roadmap = [System.IO.File]::ReadAllText((Resolve-Path $roadmapPath))
$roadmap = $roadmap.Replace('`FastCrc32.Compute`', '`FastCrc32C.Compute`')
[System.IO.File]::WriteAllText((Resolve-Path $roadmapPath), $roadmap, [System.Text.UTF8Encoding]::new($false))

# Production source must contain no superseded type or telemetry names.
$legacyProduction = @(git grep -n -E 'ExplicitOffsetWriter|FanoutPerformancePolicy|FastCrc32([^C]|$)|VerificationCrc32([^C]|$)|VerifyHash(Bytes|Time|BytesPerSecond)|RecordVerifyHash' -- dotnet/RepartoCopier.Core 2>$null)
if ($legacyProduction.Count -gt 0) {
    $legacyProduction | Write-Host
    throw 'Superseded symbols remain in productive Core source.'
}

# Real test consumers must also use the canonical CRC32C metrics; negative architecture contracts are excluded intentionally.
$legacyTests = @(git grep -n -E 'VerifyHash(Bytes|Time|BytesPerSecond)|RecordVerifyHash' -- dotnet/RepartoCopier.Core.Tests 2>$null | Where-Object { $_ -notmatch 'UnificationContractTests\.cs' })
if ($legacyTests.Count -gt 0) {
    $legacyTests | Write-Host
    throw 'Superseded verification metric names remain in active tests.'
}

dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal
git diff --check
Write-Host 'Canonical architecture and CRC32C naming verified.'
