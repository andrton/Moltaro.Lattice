param(
    [ValidateRange(1, 3)][int]$Repetitions = 3
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/RootEntryComparison.dll'
if (-not (Test-Path -LiteralPath $assembly)) {
    throw 'Build the Release project before running the comparison.'
}
$runRoot = Join-Path $projectRoot ('.local/root-entry-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void](New-Item -ItemType Directory -Path $runRoot)
$workers = [System.Collections.Generic.List[object]]::new()

function Invoke-Worker([string]$Backend, [int]$Count, [int]$Repeat) {
    $name = "$Backend-$Count-$Repeat"
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($assembly, $Backend, "$Count", (Join-Path $runRoot "$name.json"))) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['DOTNET_PROCESSOR_COUNT'] = '1'
    $start.Environment['DOTNET_TieredCompilation'] = '0'
    $start.Environment['DOTNET_gcServer'] = '0'
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(50)) {
            $process.Refresh()
            if ($process.HasExited) { break }
            if ($watch.Elapsed.TotalSeconds -gt 10 -or $process.TotalProcessorTime.TotalSeconds -gt 5) {
                throw "Worker $name exceeded its time budget: wall $($watch.Elapsed.TotalSeconds) s, CPU $($process.TotalProcessorTime.TotalSeconds) s."
            }
            if ($process.WorkingSet64 -gt 256MB) {
                throw "Worker $name exceeded its 256 MiB resident-memory guard."
            }
        }
        if ($process.ExitCode -ne 0) { throw "Worker $name failed; inspect $runRoot." }
        $sample = Get-Content -LiteralPath (Join-Path $runRoot "$name.json") -Raw | ConvertFrom-Json
        $workers.Add([pscustomobject]@{
            Name = $name
            WallMilliseconds = $watch.Elapsed.TotalMilliseconds
            CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
            PeakWorkingSetBytes = $sample.ProcessPeakWorkingSetBytes
        })
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
        }
        $process.Dispose()
    }
    Start-Sleep -Milliseconds 150
}

for ($repeat = 1; $repeat -le $Repetitions; $repeat++) {
    foreach ($count in @(32768, 262144)) {
        $backends = if ($repeat % 2 -eq 1) { @('Managed', 'Native') } else { @('Native', 'Managed') }
        foreach ($backend in $backends) {
            Invoke-Worker $backend $count $repeat
        }
    }
}
$workers | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'workers.json')
Write-Output $runRoot
