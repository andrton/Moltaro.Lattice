param(
    # Seconds each timed case runs; the builds come on top.
    [ValidateRange(1, 60)][int]$Seconds = 5,
    # Only the groups named: find, range, writes, nodes, text.
    [string[]]$Groups = @('find', 'range', 'writes', 'nodes', 'text')
)

# Runs the timing scenarios of the study of ordered structures one after another, each in a fresh
# process of the Release build, and writes their lines to the console and to a log under the ignored
# src/.local. A timing: run it on a quiet machine. README.md gives the duration of each group.

$ErrorActionPreference = 'Stop'
$srcRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$assembly = Join-Path $PSScriptRoot 'bin/Release/net10.0/SecondaryIndexComparison.dll'
if (-not (Test-Path -LiteralPath $assembly)) {
    throw 'Build the Release project before running this script.'
}
$runRoot = Join-Path $srcRoot ('.local/ordered-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
[void](New-Item -ItemType Directory -Path $runRoot)
$log = Join-Path $runRoot 'lines.txt'

$cases = [System.Collections.Generic.List[object]]::new()
$structures = @('cow', 'optimistic', 'skiplist', 'arc')

if ($Groups -contains 'find') {
    foreach ($key in @('int64', 'key16')) {
        foreach ($structure in $structures) {
            $cases.Add(@('find', '--structure', $structure, '--key', $key, '--threads', '1,16', '--writer', 'off,on', '--seconds', "$Seconds"))
        }
    }
}

if ($Groups -contains 'range') {
    foreach ($direction in @('forward', 'backward')) {
        foreach ($structure in $structures) {
            $cases.Add(@('range', '--structure', $structure, '--key', 'int64', '--direction', $direction, '--length', '100', '--threads', '1,16', '--writer', 'off,on', '--seconds', "$Seconds"))
        }
    }
}

if ($Groups -contains 'writes') {
    foreach ($structure in $structures) {
        $cases.Add(@('writes', '--structures', $structure, '--keys', 'int64', '--orders', 'random,ascending'))
    }
}

if ($Groups -contains 'nodes') {
    foreach ($node in @(256, 512, 2048, 4096)) {
        $cases.Add(@('find', '--structure', 'cow', '--key', 'int64', '--node', "$node", '--threads', '1,16', '--writer', 'on', '--seconds', "$Seconds"))
        $cases.Add(@('range', '--structure', 'cow', '--key', 'int64', '--node', "$node", '--direction', 'forward', '--threads', '16', '--writer', 'on', '--seconds', "$Seconds"))
        $cases.Add(@('writes', '--structures', 'cow', '--keys', 'int64', '--node', "$node", '--orders', 'random'))
    }
}

if ($Groups -contains 'text') {
    foreach ($text in @('random', 'numbered')) {
        foreach ($length in @(8, 32, 100)) {
            foreach ($form in @('root', 'copy', 'prefixroot', 'shared', 'sharedprefix')) {
                $operations = if ($form -eq 'copy' -or $form -eq 'shared') { 'find,range,write,readroot' } else { 'find,range,write' }
                $cases.Add(@('text', '--forms', $form, '--lengths', "$length", '--text', $text, '--operations', $operations, '--threads', '1,16', '--writer', 'off,on', '--seconds', "$Seconds"))
            }
        }
    }
}

$number = 0
foreach ($case in $cases) {
    $number++
    $arguments = @($assembly, 'ordered') + $case
    Write-Output "# $number of $($cases.Count): dotnet $($arguments[1..($arguments.Count - 1)] -join ' ')"
    $env:DOTNET_gcServer = '0'
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Case $number failed; see $log."
    }
}

Write-Output "Lines in $log"
