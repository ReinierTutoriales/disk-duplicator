$ErrorActionPreference = 'Stop'
$source = 'tools/patch-unify-clean-main-v2.ps1'
$temp = 'tools/.patch-unify-clean-main-v2-runtime.ps1'
$content = [System.IO.File]::ReadAllText((Resolve-Path $source))
$content = [regex]::Replace($content, '(?m)^\$attr = .*\r?\n', '')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $temp), $content, [System.Text.UTF8Encoding]::new($false))
try {
    & (Join-Path (Get-Location) $temp)
}
finally {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}
