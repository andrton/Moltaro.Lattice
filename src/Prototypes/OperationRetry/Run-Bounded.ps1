param(
    [string]$ConnectionFile = '.local/batch-postgres-connection.txt',
    [switch]$UniqueOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/OperationRetry.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build the Release prototype first.' }
$connection = '-'
if (-not $UniqueOnly) { $connection = (Resolve-Path -LiteralPath $ConnectionFile).Path }
$runId = [Guid]::NewGuid()
$runRoot = Join-Path $projectRoot ('.local/operation-retry-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void](New-Item -ItemType Directory -Path $runRoot)
$workers = [System.Collections.Generic.List[object]]::new()
@{ RunId = $runId.ToString(); Schema = 'lattice_retry_' + $runId.ToString('N') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'ownership.json')

function Invoke-Worker([string]$Mode, [string]$Scenario, [int]$ExpectedExit = 0) {
    $name = "$Mode-$Scenario"
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($assembly, $Mode, (Join-Path $runRoot "$name.json"), $connection, $runId.ToString(), $Scenario)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['DOTNET_PROCESSOR_COUNT'] = '1'
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
        if ($IsWindows) { $process.ProcessorAffinity = [IntPtr]1 }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        while (-not $process.WaitForExit(50)) {
            $process.Refresh()
            if ($process.HasExited) { break }
            $peak = [Math]::Max($peak, $process.WorkingSet64)
            if ($watch.Elapsed.TotalSeconds -gt 45 -or $process.TotalProcessorTime.TotalSeconds -gt 8 -or $peak -gt 192MB) {
                throw "Worker $name exceeded its resource budget."
            }
        }
        if ($process.ExitCode -ne $ExpectedExit) {
            throw "Worker $name exited $($process.ExitCode), expected $ExpectedExit. Inspect $runRoot."
        }
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
                WallMilliseconds = $watch.Elapsed.TotalMilliseconds
                CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
                SampledPeakWorkingSetBytes = $peak
            })
        }
        $process.Dispose()
    }
}

if ($UniqueOnly) {
    try { Invoke-Worker 'unique' 'indexes' }
    finally {
        $workers | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'workers.json')
        Write-Output "Results: $runRoot"
    }
    return
}

try {
    Invoke-Worker 'setup' 'schema'
    Invoke-Worker 'checks' 'protocol'
    Invoke-Worker 'unique' 'indexes'
    Invoke-Worker 'crash' 'before' 91
    Invoke-Worker 'recover' 'before'
    Invoke-Worker 'crash' 'after' 92
    Invoke-Worker 'recover' 'after'
}
finally {
    try { Invoke-Worker 'cleanup' 'schema' }
    finally {
        $workers | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'workers.json')
        Write-Output "Results: $runRoot"
    }
}
