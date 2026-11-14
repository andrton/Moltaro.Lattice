param([Parameter(Mandatory)][string] $Directory)

$ErrorActionPreference = 'Stop'
$results = @(Get-ChildItem -LiteralPath $Directory -Filter 'case-*.json' |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
if ($results.Count -eq 0) {
    throw 'No case result files were found.'
}

function Get-Median([double[]] $Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 0) {
        return ($sorted[$middle - 1] + $sorted[$middle]) / 2
    }

    return $sorted[$middle]
}

$summary = foreach ($group in ($results | Group-Object Count, KeyType, Map | Sort-Object Name)) {
    $cases = @($group.Group)
    $first = $cases[0]
    $item = [ordered]@{
        Map = $first.Map
        KeyType = $first.KeyType
        Count = $first.Count
        Processes = $cases.Count
        Capacity = $first.SteadyState.Capacity
        RetainedMiB = (Get-Median @($cases.SteadyState.RetainedIndexBytes)) / 1MB
        BytesPerEntry = Get-Median @($cases.SteadyState.BytesPerEntry)
        ResizeAtCount = $first.Growth.CountBefore
        ResizeOldCapacity = $first.Growth.OldCapacity
        ResizeNewCapacity = $first.Growth.NewCapacity
        ResizePeakMiB = (Get-Median @($cases.Growth.ControlledTransientIndexBytes)) / 1MB
        ResizeMilliseconds = Get-Median @($cases.Growth.ResizeMilliseconds)
        BuildNsPerInsert = Get-Median @($cases.SteadyState.BuildNanosecondsPerInsert)
        Verified = (@($cases | Where-Object { -not $_.CorrectnessVerified }).Count -eq 0)
    }

    foreach ($name in @('Hit', 'Miss', 'Update')) {
        $operations = @($cases | ForEach-Object { $_.SteadyState.Operations | Where-Object Operation -eq $name })
        $medians = @($operations.MedianNanosecondsPerOperation)
        $item["${name}Ns"] = Get-Median $medians
        $item["${name}ProcessMedianMinNs"] = ($medians | Measure-Object -Minimum).Minimum
        $item["${name}ProcessMedianMaxNs"] = ($medians | Measure-Object -Maximum).Maximum
        $item["${name}MaxAllocatedBytes"] = (@($operations.AllocatedBytesPerSample) | Measure-Object -Maximum).Maximum
    }

    [pscustomobject]$item
}

$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'summary.json') -Encoding utf8
$lines = @(
    '# Primary index comparison',
    '',
    'Medians across fresh processes. RAM is retained managed index memory excluding input keys; resize peak is controlled old-plus-new managed storage, not process RSS.',
    '',
    '| Keys | Count | Map | Capacity | Retained MiB | B/entry | Resize peak MiB | Hit ns | Miss ns | Update ns | Build ns/insert | Resize ms |',
    '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |'
)
foreach ($item in $summary) {
    $lines += [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        '| {0} | {1} | {2} | {3} | {4:F2} | {5:F2} | {6:F2} | {7:F1} | {8:F1} | {9:F1} | {10:F1} | {11:F2} |',
        $item.KeyType, $item.Count, $item.Map, $item.Capacity, $item.RetainedMiB,
        $item.BytesPerEntry, $item.ResizePeakMiB, $item.HitNs, $item.MissNs,
        $item.UpdateNs, $item.BuildNsPerInsert, $item.ResizeMilliseconds)
}

$lines | Set-Content -LiteralPath (Join-Path $Directory 'summary.md') -Encoding utf8
Write-Output "Summary: $(Join-Path $Directory 'summary.md')"
