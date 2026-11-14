param(
    [ValidateSet('All', 'MemoryOnly', 'PostgresOnly', 'Units', 'UnitChecks')]
    [string]$Mode = 'All',
    [string]$ConnectionFile = '.local/batch-postgres-connection.txt',
    [ValidateRange(1, 3)]
    [int]$Repeats = 2
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/WriteBatch.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build the Release prototype first.' }
$runId = [Guid]::NewGuid()
$runRoot = Join-Path $projectRoot ('.local/write-batch-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void](New-Item -ItemType Directory -Path $runRoot)
$connection = '-'
if ($Mode -ne 'MemoryOnly') {
    $connection = (Resolve-Path -LiteralPath $ConnectionFile).Path
}
$workers = [System.Collections.Generic.List[object]]::new()
@{ RunId = $runId.ToString(); Schema = 'lattice_batch_' + $runId.ToString('N'); ConnectionFile = $connection } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'ownership.json')

function Invoke-Worker([string]$WorkerMode, [string]$Scenario, [int]$ExpectedExit = 0) {
    $name = "$WorkerMode-$Scenario"
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($assembly, $WorkerMode, (Join-Path $runRoot "$name.json"), $connection, $runId.ToString(), $Scenario)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['DOTNET_PROCESSOR_COUNT'] = '1'
    $start.Environment['DOTNET_TieredCompilation'] = '0'
    $start.Environment['DOTNET_gcServer'] = '0'
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $stdout = $null
    $stderr = $null
    $peak = 0L
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $started = $process.Start()
        $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(25)) {
            $process.Refresh()
            if ($process.HasExited) { break }
            $peak = [Math]::Max($peak, $process.WorkingSet64)
            if ($watch.Elapsed.TotalSeconds -gt 15 -or $process.TotalProcessorTime.TotalSeconds -gt 5 -or $peak -gt 192MB) {
                throw "Worker $name exceeded its budget: wall=$($watch.Elapsed.TotalSeconds), CPU=$($process.TotalProcessorTime.TotalSeconds), bytes=$peak."
            }
        }
        if ($process.ExitCode -ne $ExpectedExit) { throw "Worker $name exited $($process.ExitCode), expected $ExpectedExit; inspect $runRoot." }
    }
    finally {
        if ($started) {
            if (-not $process.HasExited) {
                $process.Kill($true)
                [void]$process.WaitForExit(5000)
            }
            if ($null -ne $stdout) {
                $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot "$name.stdout.log")
                $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot "$name.stderr.log")
            }
            $workers.Add([pscustomobject]@{
                Worker = $name
                ExitCode = $process.ExitCode
                ExpectedExit = $ExpectedExit
                WallMilliseconds = $watch.Elapsed.TotalMilliseconds
                CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
                SampledPeakWorkingSetBytes = $peak
            })
        }
        $process.Dispose()
    }
    Start-Sleep -Milliseconds 150
}

$setupAttempted = $false
try {
    if ($Mode -in @('All', 'MemoryOnly')) {
        Invoke-Worker 'memory-checks' 'checks'
        Invoke-Worker 'memory-footprint' 'memory'
        for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
            Invoke-Worker 'memory-measure' "sample_$repeat"
        }
    }
    if ($Mode -ne 'MemoryOnly') {
        $setupAttempted = $true
        Invoke-Worker 'setup' 'schema'
        if ($Mode -in @('Units', 'UnitChecks')) {
            Invoke-Worker 'unit-checks' 'checks'
            if ($Mode -eq 'Units') {
                Invoke-Worker 'memory-checks' 'regression'
                Invoke-Worker 'sql-checks' 'regression'
                Invoke-Worker 'unit-footprint' 'memory'
                for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
                    Invoke-Worker 'unit-off' "sample_$repeat"
                    Invoke-Worker 'unit-on' "sample_$repeat"
                }
                $phases = @('ForegroundBeforeCommit', 'ForegroundAfterCommit', 'ForegroundMemberPublished',
                    'ForegroundAfterPublication', 'UnitsWritten', 'CursorWritten', 'AppliedCommitted', 'CleanupDeleted', 'CleanupCommitted')
                for ($index = 0; $index -lt $phases.Length; $index++) {
                    Invoke-Worker 'unit-crash' $phases[$index] (110 + $index)
                    Invoke-Worker 'unit-recover' $phases[$index]
                }
            }
        }
        else {
            Invoke-Worker 'sql-checks' 'checks'
            Invoke-Worker 'sql-footprint' 'memory'
            for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
                Invoke-Worker 'sql-measure' "sample_$repeat"
            }
            $phases = @('BeforeCommit', 'AfterCommit', 'MemberPublished', 'AfterPublication')
            for ($index = 0; $index -lt $phases.Length; $index++) {
                Invoke-Worker 'crash' $phases[$index] (92 + $index)
                Invoke-Worker 'recover' $phases[$index]
            }
        }
    }
}
finally {
    try {
        if ($setupAttempted) { Invoke-Worker 'cleanup' 'schema' }
    }
    finally {
        $workers | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'workers.json')
    }
}
Write-Output $runRoot
