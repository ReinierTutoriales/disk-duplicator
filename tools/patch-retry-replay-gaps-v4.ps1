$ErrorActionPreference = 'Stop'

$validatedRef = '38c451ee2026aabfb16f3d0976fe022614a10cbd'
$files = @(
    'patch-retry-replay-gaps.ps1',
    'patch-retry-replay-gaps-v2.ps1',
    'patch-retry-replay-gaps-v3.ps1'
)

try {
    foreach ($name in $files) {
        $repoPath = "tools/$name"
        $content = git show "$validatedRef`:$repoPath"
        if ($LASTEXITCODE -ne 0) { throw "Cannot recover validated patch $repoPath from $validatedRef" }
        [System.IO.File]::WriteAllLines(
            (Join-Path (Get-Location) $repoPath),
            [string[]]$content,
            [System.Text.UTF8Encoding]::new($false))
    }

    & (Join-Path (Get-Location) 'tools/patch-retry-replay-gaps-v3.ps1')
}
finally {
    foreach ($name in $files) {
        Remove-Item -LiteralPath (Join-Path (Get-Location) "tools/$name") -Force -ErrorAction SilentlyContinue
    }
}

dotnet format whitespace RepartoCopier.sln --no-restore --verbosity minimal
git diff --check
Write-Host 'Previously validated retry/replay patch reconstructed and applied.'
