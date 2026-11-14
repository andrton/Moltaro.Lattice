param(
    [ValidateSet('directed', 'generated', 'negative', 'threaded')]
    [string[]]$Groups = @('directed', 'generated', 'negative', 'threaded')
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/ReplicaBootstrap.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw 'Build the Release project first.' }
$runRoot = Join-Path $projectRoot ('.local/replica-bootstrap-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void](New-Item -ItemType Directory -Path $runRoot)
$workers = [System.Collections.Generic.List[object]]::new()

foreach ($group in $Groups) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($assembly, $group, (Join-Path $runRoot "$group.json"))) {
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
    try {
        $started = $process.Start()
        $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(50)) {
            $process.Refresh()
            if ($process.HasExited) { break }
            if ($watch.Elapsed.TotalSeconds -gt 10 -or $process.TotalProcessorTime.TotalSeconds -gt 3) {
                throw "Group $group exceeded its 10-second wall/3-second CPU budget."
            }
            if ($process.WorkingSet64 -gt 192MB) { throw "Group $group exceeded its 192 MiB memory guard." }
        }
        if ($process.ExitCode -ne 0) { throw "Group $group failed; inspect $runRoot." }
        $workers.Add([pscustomobject]@{
            Group = $group
            WallMilliseconds = $watch.Elapsed.TotalMilliseconds
            CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
        })
    }
    finally {
        if ($started) {
            if (-not $process.HasExited) {
                $process.Kill($true)
                [void]$process.WaitForExit(5000)
            }
            if ($null -ne $stdout) {
                $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot "$group.stdout.log")
                $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot "$group.stderr.log")
            }
        }
        $process.Dispose()
    }
    Start-Sleep -Milliseconds 100
}
$workers | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'workers.json')
Write-Output $runRoot
