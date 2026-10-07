# DiskSpd file targets only. Read mode never writes the target. Write mode creates
# and removes one private test file on the selected volume; never use raw disks.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z]$')] [string] $Drive,
    [ValidateSet('Read', 'Write')] [string] $Mode = 'Read',
    [string] $ReadFile,
    [string] $DiskSpd = 'diskspd.exe',
    [int[]] $ReadThreads = @(1, 2, 4),
    [int[]] $QueueDepths = @(1, 4, 8),
    [ValidateRange(1, 300)] [int] $Seconds = 30,
    [ValidateRange(1, 256)] [int] $FileGiB = 8,
    [ValidateRange(1, 10)] [int] $Repeats = 2,
    [ValidateRange(0, 3600)] [int] $RestSeconds = 90,
    [switch] $Curve,
    [string] $OutputDirectory = (Join-Path (Get-Location) 'diskspd-results')
)

$ErrorActionPreference = 'Stop'
$volume = "$($Drive.ToUpperInvariant()):\"
if ($null -eq (Get-Command $DiskSpd -ErrorAction SilentlyContinue)) {
    throw "DiskSpd no está disponible: $DiskSpd"
}
if ($Mode -eq 'Read') {
    if ([string]::IsNullOrWhiteSpace($ReadFile)) { throw 'Read requiere -ReadFile.' }
    $readItem = Get-Item -LiteralPath $ReadFile -ErrorAction Stop
    if ($readItem.PSIsContainer -or $readItem.Length -lt 8388608) { throw 'ReadFile debe ser un archivo de al menos 8 MiB.' }
    if (-not $readItem.FullName.StartsWith($volume, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ReadFile debe estar en la letra indicada por -Drive.'
    }
    if (@($ReadThreads | Where-Object { $_ -lt 1 -or $_ -gt 4 }).Count -gt 0) { throw 'ReadThreads admite 1 a 4.' }
    $targets = @($ReadThreads)
} else {
    if ($ReadFile) { throw 'Write no admite -ReadFile.' }
    if (@($QueueDepths | Where-Object { $_ -lt 1 -or $_ -gt 32 }).Count -gt 0) { throw 'QueueDepths admite 1 a 32.' }
    $targets = @($QueueDepths)
}
if ($targets.Count -eq 0 -or @($targets | Select-Object -Unique).Count -ne $targets.Count) {
    throw 'La lista de hilos o QD debe contener valores distintos.'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = Join-Path $OutputDirectory ('run-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop | Out-Null
$privateDirectory = $null
$testFile = $null
if ($Mode -eq 'Write') {
    $free = (Get-PSDrive -Name $Drive).Free
    $required = ([long]$FileGiB + 1L) * 1GB
    if ($null -eq $free -or $free -lt $required) { throw "Se requieren al menos $required bytes libres en $volume." }
    $privateDirectory = Join-Path $volume ('.disk-duplicator-bench-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $privateDirectory -ErrorAction Stop | Out-Null
    $testFile = Join-Path $privateDirectory 'test.dat'
}

$rows = [System.Collections.Generic.List[object]]::new()
try {
    $first = $targets[0]
    for ($pass = 0; $pass -le $Repeats; $pass++) {
        $isControl = $pass -eq $Repeats
        [int[]] $order = if ($isControl) { @($first) } else { @($targets) }
        if (-not $isControl -and ($pass % 2) -eq 1) { [Array]::Reverse($order) }
        foreach ($value in $order) {
            $name = '{0}-pass{1}-{2}-{3}' -f $Mode.ToLowerInvariant(), ($pass + 1), $value, ([Guid]::NewGuid().ToString('N'))
            $output = Join-Path $OutputDirectory ($name + $(if ($Curve) { '.xml' } else { '.txt' }))
            $diskSpdArguments = @('-b8M', '-Su', '-fs', '-o1', "-d$Seconds", '-W5', '-D1000')
            if ($Curve) { $diskSpdArguments += '-Rxml' }
            if ($Mode -eq 'Read') {
                $diskSpdArguments += "-t$value"
                $diskSpdArguments += '-w0'
                $diskSpdArguments += $readItem.FullName
            } else {
                $diskSpdArguments += '-t1'
                $diskSpdArguments += "-o$value"
                $diskSpdArguments += '-w100'
                $diskSpdArguments += '-Z256M'
                if (-not (Test-Path -LiteralPath $testFile)) { $diskSpdArguments += "-c$($FileGiB)G" }
                $diskSpdArguments += $testFile
            }
            $started = Get-Date
            $raw = (& $DiskSpd @diskSpdArguments 2>&1 | Out-String)
            $code = $LASTEXITCODE
            [System.IO.File]::WriteAllText($output, $raw)
            if ($code -ne 0) { throw "DiskSpd devolvió $code. Informe: $output" }
            $rate = $null
            if (-not $Curve) {
                $match = [regex]::Match($raw, '(?ms)^Total IO\s+.*?^\s*total:\s*\d+\s*\|\s*\d+\s*\|\s*([0-9]+(?:\.[0-9]+)?)')
                if ($match.Success) { $rate = [double]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) }
                else { Write-Warning "No se pudo leer la velocidad; conserva $output" }
            }
            $rows.Add([pscustomobject]@{ Mode = $Mode; Pass = $pass + 1; Control = $isControl; Value = $value;
                MiBps = $rate; StartedAt = $started.ToString('o'); Raw = $output })
            if ($RestSeconds -gt 0 -and -not $isControl) { Start-Sleep -Seconds $RestSeconds }
        }
    }
    $csv = Join-Path $OutputDirectory 'runs.csv'
    $rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
    $usable = @($rows | Where-Object { -not $_.Control -and $null -ne $_.MiBps })
    $summary = foreach ($group in ($usable | Group-Object Value)) {
        $rates = @($group.Group | ForEach-Object { $_.MiBps } | Sort-Object)
        $middle = [int][Math]::Floor($rates.Count / 2)
        $median = if (($rates.Count % 2) -eq 0) { ($rates[$middle - 1] + $rates[$middle]) / 2 } else { $rates[$middle] }
        [pscustomobject]@{ Mode = $Mode; Value = $group.Name; MedianMiBps = $median; Samples = $rates.Count }
    }
    if ($summary) { $summary | Export-Csv -Path (Join-Path $OutputDirectory 'summary.csv') -NoTypeInformation -Encoding UTF8 }
    $baseline = $rows[0].MiBps
    $final = $rows[$rows.Count - 1].MiBps
    if ($null -ne $baseline -and $baseline -gt 0 -and $null -ne $final -and
        [Math]::Abs($final - $baseline) / $baseline -gt 0.05) {
        Write-Warning 'La corrida de control difiere más de 5 % de la primera. No asumas caché o temperatura recuperadas.'
    }
    $rows | Format-Table -AutoSize
    Write-Host "Resultados: $csv"
    if ($Curve) { Write-Host 'Los XML conservan la serie temporal. No se calcula una mediana desde un resumen XML no validado.' }
} finally {
    if ($privateDirectory -and (Test-Path -LiteralPath $privateDirectory)) {
        if ($testFile -and (Test-Path -LiteralPath $testFile)) { Remove-Item -LiteralPath $testFile -Force }
        Remove-Item -LiteralPath $privateDirectory -Force
    }
}
