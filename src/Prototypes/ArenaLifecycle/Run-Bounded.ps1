param(
    [ValidateSet('directed', 'churn', 'concurrent-off', 'concurrent-on', 'index-checks', 'index-memory', 'indexed-off', 'indexed-on', 'tail-checks', 'tail-off', 'tail-on')]
    [string[]]$Groups = @('directed', 'churn', 'concurrent-off', 'concurrent-on'),
    [ValidateRange(1, 3)]
    [int]$ConcurrentRepeats = 3
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/ArenaLifecycle.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build the Release project first.' }
$runRoot = Join-Path $projectRoot ('.local/arena-lifecycle-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void](New-Item -ItemType Directory -Path $runRoot)
$workers = [System.Collections.Generic.List[object]]::new()

for ($repeat = 1; $repeat -le $ConcurrentRepeats; $repeat++) {
    foreach ($group in $Groups) {
        $repeatable = $group.StartsWith('concurrent-') -or $group.StartsWith('indexed-') -or $group -eq 'index-memory' -or $group -in @('tail-off', 'tail-on')
        if ($repeat -gt 1 -and -not $repeatable) { continue }
        $name = "$group-$repeat"
        $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @($assembly, $group, (Join-Path $runRoot "$name.json"))) {
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
        $sampledPeak = 0L
        try {
            $started = $process.Start()
            $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            while (-not $process.WaitForExit(25)) {
                $process.Refresh()
                if ($process.HasExited) { break }
                $sampledPeak = [Math]::Max($sampledPeak, $process.WorkingSet64)
                if ($watch.Elapsed.TotalSeconds -gt 15 -or $process.TotalProcessorTime.TotalSeconds -gt 5) {
                    throw "Worker $name exceeded its 15-second wall/5-second CPU budget."
                }
                if ($process.WorkingSet64 -gt 192MB) { throw "Worker $name exceeded its 192 MiB sampled working-set guard." }
            }
            if ($process.ExitCode -ne 0) { throw "Worker $name failed; inspect $runRoot." }
            $workers.Add([pscustomobject]@{
                Worker = $name
                WallMilliseconds = $watch.Elapsed.TotalMilliseconds
                CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
                SampledPeakWorkingSetBytes = $sampledPeak
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
}
$workers | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'workers.json')
Write-Output $runRoot
