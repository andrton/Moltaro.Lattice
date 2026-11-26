# Runs the preregistered Release comparison after the clean and diagnostic copies are built.
# Each timing process performs and validates its own fresh untimed diagnostic witness.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseDriver,
    [Parameter(Mandatory)][string]$BaseDiagnosticDriver,
    [Parameter(Mandatory)][string]$CandidateDriver,
    [Parameter(Mandatory)][string]$CandidateDiagnosticDriver,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$CandidateRevision,
    [Parameter(Mandatory)][long]$CandidateTightLimit,
    [Parameter(Mandatory)][long]$CandidateControlLimit,
    [Parameter(Mandatory)][string]$NewRunDirectory,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$base = @{
    Name = 'base'; Revision = 'dd519ba40005204636ad6ff7ca08a3667ab1a3d3'
    Driver = (Resolve-Path -LiteralPath $BaseDriver).Path
    Diagnostic = (Resolve-Path -LiteralPath $BaseDiagnosticDriver).Path
    Tight = 3L * 65536; Control = 5L * 65536
}
$candidate = @{
    Name = 'candidate'; Revision = $CandidateRevision
    Driver = (Resolve-Path -LiteralPath $CandidateDriver).Path
    Diagnostic = (Resolve-Path -LiteralPath $CandidateDiagnosticDriver).Path
    Tight = $CandidateTightLimit; Control = $CandidateControlLimit
}
if ($CandidateTightLimit -le 0 -or $CandidateControlLimit -le $CandidateTightLimit) {
    throw 'Provide the independently established candidate limits before starting the comparison.'
}
$root = [IO.Path]::GetFullPath($NewRunDirectory)
if (Test-Path -LiteralPath $root) { throw 'Choose a new comparison output directory.' }
New-Item -ItemType Directory -Path $root | Out-Null

function Get-LaneLimit([hashtable]$Engine, [string]$Lane) {
    if ($Lane.EndsWith('-tight')) { return $Engine.Tight }
    return $Engine.Control
}

function Invoke-Diagnostic([hashtable]$Engine, [string]$Lane) {
    $dir = Join-Path $root ("preflight-{0}-{1}" -f $Lane, $Engine.Name)
    New-Item -ItemType Directory -Path $dir | Out-Null
    $limit = Get-LaneLimit $Engine $Lane
    Push-Location $dir
    try {
        dotnet $Engine.Driver s14-reserve-preflight development-5 $Lane $limit $Engine.Revision $Engine.Diagnostic 2>&1 |
            Tee-Object -FilePath "$dir/console.txt" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Untimed standing-reserve preflight failed in $dir." }
        $raw = @(Get-ChildItem -Path '.local/measurements/s14-reserve-preflight-*/branch-receipt.json' -File)
        if ($raw.Count -ne 1) { throw "Could not identify the diagnostic receipt in $dir." }
        $receipt = Get-Content -LiteralPath $raw[0].FullName -Raw | ConvertFrom-Json
        if ($receipt.Limit -ne $limit -or $receipt.Revision -ne $Engine.Revision -or $receipt.Lane -ne $Lane) {
            throw "Diagnostic identity differs from the requested limit, lane or product revision in $dir."
        }
        return $receipt
    }
    finally {
        Pop-Location
    }
}

function Invoke-Round([hashtable]$Engine, [string]$Lane, [int]$Round, [int]$Order) {
    $dir = Join-Path $root ("{0}-{1:00}-{2}-{3}" -f $Lane, $Round, $Order, $Engine.Name)
    New-Item -ItemType Directory -Path $dir | Out-Null
    $limit = Get-LaneLimit $Engine $Lane
    Push-Location $dir
    try {
        dotnet $Engine.Driver s14-standing-reserve development-5 $Lane $limit $Round $Engine.Revision $Engine.Diagnostic |
            Tee-Object -FilePath "$dir/console.txt" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Standing-reserve comparison failed in $dir." }
        $raw = @(Get-ChildItem -Path '.local/measurements/s14-standing-reserve-*/reserve.json' -File)
        if ($raw.Count -ne 1) { throw "Could not identify the sole raw timing report in $dir." }
        return Get-Content -LiteralPath $raw[0].FullName -Raw | ConvertFrom-Json
    }
    finally {
        Pop-Location
    }
}

function Get-CapacityShape($state) {
    return [ordered]@{
        Regions = $state.Regions; HeldNativeBytes = $state.Native.Held
        RootCount = $state.RootCount; LiveIndexNodes = $state.LiveIndexNodes
        HeldNodes = $state.Pool.Count; StandingTarget = $state.StandingTarget
    }
}

function Get-ApprovedOwnerShapes([string]$lane) {
    $text = 'Moltaro.Lattice.Storage.Text.'
    $textPages = $text + 'TextPages`1['
    $roots = 'Moltaro.Lattice.Storage.Roots.'
    $owners = @{
        'Root table metadata/Roots' = @{ Type = $roots + 'RootTable'; Base = 288; Candidate = 344 }
        'Root table metadata/Roots._smallerDirectories' = @{
            Type = $roots + 'RootEntry[][][]'; Base = 0; Candidate = 184; AddedArrayLength = 20
        }
        'Text metadata/Texts' = @{ Type = $text + 'TextTable'; Base = 80; Candidate = 88 }
        'Text metadata/Texts._locator' = @{ Type = $text + 'TextLocator'; Base = 40; Candidate = 48 }
        'Text metadata/Texts._locator._pages' = @{ Type = $textPages + $text + 'TextLocator+Leaf]'; Base = 32; Candidate = 40 }
        'Text metadata/Texts._locator._pages._root' = @{ Type = $text + 'TextLocator+Leaf'; Base = 48; Candidate = 56 }
        'Text metadata/Texts._slots' = @{ Type = $text + 'TextWriterSlots'; Base = 32; Candidate = 56 }
        'Text metadata/Texts._slots._pages' = @{ Type = $textPages + $text + 'TextSlot[]]'; Base = 32; Candidate = 40 }
        'Text metadata/Texts._active' = @{ Type = $text + 'TextBucketSet'; Base = 40; Candidate = 48 }
        'Text metadata/Texts._active._pages' = @{ Type = $textPages + $text + 'TextBucketSet+Page]'; Base = 32; Candidate = 40 }
    }
    $entities = @('Indexed', 'Probe', 'Filler')
    if ($lane.StartsWith('six-')) { $entities += 'TextFiller' }
    foreach ($entity in $entities) {
        $field = 'Primary index tables/' + $entity + '._index._table'
        $owners[$field] = @{ Type = 'Moltaro.Lattice.Storage.Hashing.IndexTable'; Base = 304; Candidate = 312 }
        $owners[$field + '._overflow'] = @{ Type = 'Moltaro.Lattice.Storage.Hashing.OverflowBuckets'; Base = 40; Candidate = 64 }
    }
    return $owners
}

function Assert-ManagedShape($baseState, $candidateState, [string]$lane, [string]$boundary) {
    $context = "$lane $boundary"
    $approved = Get-ApprovedOwnerShapes $lane
    $baseFields = $baseState.ManagedShape.PSObject.Properties
    $candidateFields = $candidateState.ManagedShape.PSObject.Properties
    $baseTotals = $baseState.ManagedBackingBytes.PSObject.Properties
    $candidateTotals = $candidateState.ManagedBackingBytes.PSObject.Properties
    if ($null -eq $baseFields -or $null -eq $candidateFields -or @($baseFields).Count -ne @($candidateFields).Count -or
        $null -eq $baseTotals -or $null -eq $candidateTotals -or @($baseTotals).Count -ne @($candidateTotals).Count) {
        throw "Different or missing managed capacity fields at $context."
    }

    foreach ($field in $approved.Keys) {
        if ($null -eq $baseFields[$field] -or $null -eq $candidateFields[$field]) {
            throw "Missing approved structural field $field at $context."
        }
    }

    $baseAccounted = @{}
    $candidateAccounted = @{}
    $approvedDelta = @{}
    foreach ($field in $baseFields) {
        $left = $field.Value
        $candidateField = $candidateFields[$field.Name]
        $right = $candidateField.Value
        if ($null -eq $right -or $candidateField.Name -cne $field.Name -or
            $field.Name -cne ($left.Category + '/' + $left.Field) -or
            $left.Category -cne $right.Category -or $left.Field -cne $right.Field) {
            throw "Managed field identity differs for $($field.Name) at $context."
        }

        if ($approved.ContainsKey($field.Name)) {
            $owner = $approved[$field.Name]
            $oldType = $owner.Type
            $oldCount = 1
            if ($owner.ContainsKey('AddedArrayLength')) {
                $oldType = $null
                $oldCount = 0
            }
            if ($left.ConcreteType -cne $oldType -or $right.ConcreteType -cne $owner.Type -or
                $left.Count -ne $oldCount -or $right.Count -ne 1 -or
                $null -ne $left.Length -or $right.Length -ne $owner.AddedArrayLength -or
                $left.ActualBytes -ne $owner.Base -or $right.ActualBytes -ne $owner.Candidate -or
                $left.AccountedBytes -ne $owner.Base -or $right.AccountedBytes -ne $owner.Candidate) {
                throw "Unexpected approved field type, count, length or size for $($field.Name) at $context."
            }
            $approvedDelta[$left.Category] += $owner.Candidate - $owner.Base
        } else {
            if ($left.ConcreteType -cne $right.ConcreteType -or $left.Count -ne $right.Count -or $left.Length -ne $right.Length) {
                throw "Managed field type, count or array length differs for $($field.Name) at $context."
            }
            if ($left.ActualBytes -ne $right.ActualBytes -or $left.AccountedBytes -ne $right.AccountedBytes) {
                throw "Managed field bytes differ for $($field.Name) at $context."
            }
        }

        $baseAccounted[$left.Category] += $left.AccountedBytes
        $candidateAccounted[$left.Category] += $right.AccountedBytes
    }

    if ($baseAccounted.Count -ne @($baseTotals).Count -or $candidateAccounted.Count -ne @($candidateTotals).Count) {
        throw "Unexpected managed capacity categories at $context."
    }

    foreach ($category in $baseTotals) {
        $name = $category.Name
        $other = $candidateTotals[$name]
        if ($null -eq $other -or !$baseAccounted.ContainsKey($name) -or !$candidateAccounted.ContainsKey($name) -or
            $category.Value -ne $baseAccounted[$name] -or $other.Value -ne $candidateAccounted[$name] -or
            $other.Value - $category.Value -ne [long]$approvedDelta[$name]) {
            throw "Unreconciled managed capacity for $name at $context."
        }
    }
}

function Get-EventSignature($events) {
    foreach ($event in $events) {
        if ($event.Kind -in @('slot-refused', 'text-slot-refused')) {
            "{0}:{1}" -f $event.Kind, $event.Address
        } else {
            $event.Kind
        }
    }
}

function ConvertTo-DiagnosticWork($Receipt) {
    foreach ($witness in $Receipt.Witnesses) {
        $snapshots = @($witness.Before, $witness.AfterUnindexed, $witness.AfterIndexed)
        if (@($snapshots | Where-Object EffectiveLimit -ne $Receipt.Limit).Count -ne 0) {
            throw "The effective limit differs from the pinned $($Receipt.Lane) preflight limit."
        }

        [ordered]@{
            Before = Get-CapacityShape $witness.Before
            AfterUnindexed = Get-CapacityShape $witness.AfterUnindexed
            AfterIndexed = Get-CapacityShape $witness.AfterIndexed
            UnindexedEvents = @(Get-EventSignature $witness.Unindexed)
            IndexedEvents = @(Get-EventSignature $witness.Indexed)
            UnindexedChanged = $witness.UnindexedChanged; UnindexedRefusal = $witness.UnindexedRefusal
            UnindexedPositionAdvance = $witness.UnindexedPosition - $witness.BeforePosition
            IndexedPositionAdvance = $witness.IndexedPosition - $witness.UnindexedPosition
        } | ConvertTo-Json -Depth 10 -Compress
    }
}

function ConvertTo-MatchingWork($Report) {
    return [ordered]@{
        Capacity = Get-CapacityShape $Report.Initial
        Outcomes = @($Report.Distributions | Select-Object Name, Outcome, Offered, Accepted, Refused, Completed)
    } | ConvertTo-Json -Depth 10 -Compress -AsArray
}

$launcher = Get-Process -Id $PID
$previous = $launcher.ProcessorAffinity
try {
    $launcher.ProcessorAffinity = [IntPtr]0xFF
    $lanes = @('two-tight', 'six-tight', 'two-control', 'six-control')
    $mismatches = [Collections.Generic.List[string]]::new()
    foreach ($lane in $lanes) {
        $basePreflight = Invoke-Diagnostic $base $lane
        $candidatePreflight = Invoke-Diagnostic $candidate $lane
        for ($pair = 0; $pair -lt $basePreflight.Witnesses.Count; $pair++) {
            foreach ($stage in @('Before', 'AfterUnindexed', 'AfterIndexed')) {
                Assert-ManagedShape $basePreflight.Witnesses[$pair].$stage $candidatePreflight.Witnesses[$pair].$stage $lane "$pair $stage"
            }
        }
        if (((ConvertTo-DiagnosticWork $basePreflight) -join [char]10) -ne
            ((ConvertTo-DiagnosticWork $candidatePreflight) -join [char]10)) {
            $mismatches.Add($lane)
        }
    }

    if ($mismatches.Count -ne 0) {
        throw "Non-equivalent starting topology, capacity or admission branches in $($mismatches -join ', '). Untimed receipts remain in $root."
    }

    if ($PreflightOnly) {
        Write-Host "Untimed equivalence checks passed. Receipts remain in $root."
        return
    }

    foreach ($lane in $lanes) {
        foreach ($round in 1..5) {
            $order = if ($round % 2 -eq 1) { @($base, $candidate) } else { @($candidate, $base) }
            $first = Invoke-Round $order[0] $lane $round 1
            $second = Invoke-Round $order[1] $lane $round 2
            $baseReport, $candidateReport = if ($order[0].Name -eq 'base') { $first, $second } else { $second, $first }
            Assert-ManagedShape $baseReport.Initial $candidateReport.Initial $lane "round $round initial"
            if ((ConvertTo-MatchingWork $first) -ne (ConvertTo-MatchingWork $second)) {
                throw "Non-equivalent starting capacity or completed work in $lane round $round. Raw outcomes remain in $root."
            }
        }
    }
}
finally {
    $launcher.ProcessorAffinity = $previous
}
