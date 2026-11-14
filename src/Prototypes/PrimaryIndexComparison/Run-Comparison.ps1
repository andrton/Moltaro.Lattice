param(
    [int[]] $Counts = @(100000, 1000000),
    [ValidateSet('Blitz', 'Dense', 'ArcSlim', 'Arc')]
    [string[]] $Maps = @('Blitz', 'Dense', 'ArcSlim', 'Arc'),
    [ValidateSet('Int64', 'Guid', 'String')]
    [string[]] $Keys = @('Int64', 'Guid', 'String'),
    [ValidateRange(1, 5)]
    [int] $ProcessRepetitions = 3,
    [ValidateRange(3, 15)]
    [int] $Samples = 5,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$binary = Join-Path $PSScriptRoot 'bin/Release/net10.0/PrimaryIndexComparison.dll'
if (-not (Test-Path -LiteralPath $binary)) {
    throw 'Build PrimaryIndexComparison in Release before running this script.'
}

foreach ($count in $Counts) {
    if ($count -lt 1024 -or $count -gt 2000000) {
        throw 'Counts must be between 1,024 and 2,000,000.'
    }
}

if (-not $OutputDirectory) {
    $runName = 'primary-index-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $projectRoot ".local/$runName"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory"
}

New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$completed = 0
$total = $Counts.Count * $Maps.Count * $Keys.Count * $ProcessRepetitions
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
Write-Output "Sequential comparison: $total processes; results: $OutputDirectory"

for ($repetition = 0; $repetition -lt $ProcessRepetitions; $repetition++) {
    $orderedMaps = @($Maps)
    if ($repetition % 2 -eq 1) {
        [Array]::Reverse($orderedMaps)
    }
    elseif ($repetition -gt 0 -and $Maps.Count -gt 1) {
        $orderedMaps = @($Maps[1..($Maps.Count - 1)]) + @($Maps[0])
    }

    foreach ($count in $Counts) {
        foreach ($key in $Keys) {
            foreach ($map in $orderedMaps) {
                $caseName = "case-$repetition-$count-$key-$map"
                $resultPath = Join-Path $OutputDirectory "$caseName.json"
                $startInfo = [Diagnostics.ProcessStartInfo]::new($dotnet)
                $startInfo.UseShellExecute = $false
                $startInfo.CreateNoWindow = $true
                $startInfo.RedirectStandardOutput = $true
                $startInfo.RedirectStandardError = $true
                $startInfo.Environment['DOTNET_TieredCompilation'] = '0'
                $startInfo.Environment['DOTNET_gcServer'] = '0'
                foreach ($argument in @($binary, 'sample', $map, $key, "$count", "$Samples", $resultPath)) {
                    $startInfo.ArgumentList.Add($argument)
                }

                $process = [Diagnostics.Process]::Start($startInfo)
                try {
                    $outputTask = $process.StandardOutput.ReadToEndAsync()
                    $errorTask = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(120000)) {
                        $process.Kill($true)
                        $process.WaitForExit()
                        throw "Worker timed out: $caseName"
                    }

                    $output = $outputTask.GetAwaiter().GetResult()
                    $errorOutput = $errorTask.GetAwaiter().GetResult()
                    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$caseName.stdout.log"), $output)
                    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$caseName.stderr.log"), $errorOutput)
                    if ($process.ExitCode -ne 0) {
                        throw "Worker failed: $caseName`n$errorOutput"
                    }

                    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                    if (-not $result.CorrectnessVerified -or -not $result.Growth.NoGcRegionCompleted) {
                        throw "Worker verification missing: $caseName"
                    }

                    $completed++
                    Write-Output "[$completed/$total] $map / $key / $count verified ($([int]$stopwatch.Elapsed.TotalSeconds)s elapsed)"
                }
                finally {
                    if (-not $process.HasExited) {
                        $process.Kill($true)
                        $process.WaitForExit()
                    }

                    $process.Dispose()
                }
            }
        }
    }
}

& (Join-Path $PSScriptRoot 'Summarize-Results.ps1') -Directory $OutputDirectory
Write-Output "Completed $completed verified processes in $([int]$stopwatch.Elapsed.TotalSeconds)s."
