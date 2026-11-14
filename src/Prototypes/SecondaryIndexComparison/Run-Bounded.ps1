param(
    [ValidateRange(1024, 20000)][int]$Count = 10000,
    [ValidateRange(1, 3)][int]$Repetitions = 3
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/SecondaryIndexComparison.dll'
if (-not (Test-Path -LiteralPath $assembly)) {
    throw 'Build the Release project before running this script.'
}
$runRoot = Join-Path $projectRoot ('.local/secondary-index-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
[void](New-Item -ItemType Directory -Path $runRoot)
$workerResults = [System.Collections.Generic.List[object]]::new()

function Invoke-Worker([string]$Name, [string[]]$WorkerArguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($assembly)
    foreach ($argument in $WorkerArguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['DOTNET_PROCESSOR_COUNT'] = '2'
    $start.Environment['DOTNET_TieredCompilation'] = '0'
    $start.Environment['DOTNET_gcServer'] = '0'
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $process.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(20000)) {
            throw "Worker $Name exceeded its 20-second budget."
        }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot "$Name.stdout.log")
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $runRoot "$Name.stderr.log")
        if ($process.ExitCode -ne 0) { throw "Worker $Name failed; inspect its stderr log in $runRoot." }
        $workerResults.Add([pscustomobject]@{
            Name = $Name
            ElapsedMilliseconds = ($process.ExitTime - $process.StartTime).TotalMilliseconds
            CpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds
        })
    }
    finally {
        if ($process.Id -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
    Start-Sleep -Milliseconds 150
}

for ($repeat = 1; $repeat -le $Repetitions; $repeat++) {
    $name = "checks-$repeat"
    Invoke-Worker $name @('checks', "$Count", (Join-Path $runRoot "$name.json"))
    foreach ($distribution in @('Unique', 'Repeated')) {
        # Alternate order between repetitions to reduce systematic warm/cache bias.
        $maps = if ($repeat % 2 -eq 1) { @('Arc', 'Kaos') } else { @('Kaos', 'Arc') }
        foreach ($map in $maps) {
            $name = "$map-$distribution-$repeat"
            Invoke-Worker $name @('sample', $map, $distribution, "$Count", (Join-Path $runRoot "$name.json"))
        }
    }
}
$workerResults | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'workers.json')
Write-Output $runRoot
