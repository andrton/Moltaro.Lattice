# Builds one disposable Release comparison copy from a committed product revision.
# Run twice per engine: first Clean, then Diagnostic with the clean engine DLL.
# The source checkout is only read. No runtime instrumentation enters the product history.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceCheckout,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Revision,
    [Parameter(Mandatory)][string]$NewComparisonDirectory,
    [Parameter(Mandatory)][ValidateSet('Clean', 'Diagnostic')][string]$Mode,
    [string]$CleanEngineDll
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceCheckout).Path
$destination = [IO.Path]::GetFullPath($NewComparisonDirectory)
if ((Test-Path -LiteralPath $destination) -or
    $destination.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a new comparison directory outside the source checkout.'
}
if ((git -C $source rev-parse HEAD).Trim() -ne $Revision) {
    throw 'The source checkout is not at the exact requested revision.'
}

New-Item -ItemType Directory -Path $destination | Out-Null
$archive = Join-Path $destination 'exact-source.zip'
git -C $source archive --format=zip --output=$archive $Revision
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the exact committed source.' }
Expand-Archive -LiteralPath $archive -DestinationPath $destination

$measurements = Join-Path $destination 'src/Moltaro.Lattice.Measurements'
$churn = Join-Path $measurements 'Scenarios/StorageChurn'
$engine = Join-Path $destination 'src/Moltaro.Lattice.Engine/Engine'
foreach ($file in @('ChurnLedger.cs', 'ManagedBackingSize.cs', 'MeasurementEnvironment.cs', 'StandingReserveInventory.cs',
        'StandingReserveScenario.cs', 'StandingReserveTrace.cs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $churn
}

function Update-Exact([string]$Path, [string]$Anchor, [string]$Replacement) {
    $nl = [Environment]::NewLine
    $original = [IO.File]::ReadAllText($Path).Replace([string][char]13, '')
    $anchorLf = $Anchor.Replace([string][char]13, '')
    if ([regex]::Matches($original, [regex]::Escape($anchorLf)).Count -ne 1) {
        throw "The diagnostic anchor changed in $Path. Review the symmetric insertion before rebuilding."
    }
    $next = $original.Replace($anchorLf, $Replacement.Replace([string][char]13, ''))
    [IO.File]::WriteAllText($Path, $next.Replace([string][char]10, $nl))
}

if ($Mode -eq 'Diagnostic') {
    $clean = (Resolve-Path -LiteralPath $CleanEngineDll).Path
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($clean)
    if (!$info.ProductVersion.EndsWith($Revision, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The specified clean engine artifact was built from a different revision.'
    }
    $hash = (Get-FileHash -LiteralPath $clean -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceFile = Join-Path $engine 'StandingReserveDiagnostics.cs'
    [IO.File]::WriteAllText($sourceFile, @"
using System.Runtime.InteropServices;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine;

internal static class StandingReserveDiagnostics
{
    public const string Revision = "$Revision";
    public const string CleanEngineSha256 = "$hash";
    public static Action<string, ulong>? Observe = null;

    public static List<ulong>? Nodes(Arena arena, ArenaAddress head)
    {
        if (Observe is null)
        {
            return null;
        }

        var nodes = new List<ulong>();
        while (!head.IsNull)
        {
            nodes.Add(head.Value);
            head = MemoryMarshal.Read<ArenaAddress>(arena.Payload(head));
        }
        return nodes;
    }
}
"@)

    $coordinator = Join-Path $engine 'Writes/WriteCoordinator.cs'
    Update-Exact $coordinator @'
    private PreparationEnum TryPrepare(PendingWrite write, ref Hold hold, out PreparedWrite? prepared, out LatticeException? refusal)
    {
'@ @'
    private PreparationEnum TryPrepare(PendingWrite write, ref Hold hold, out PreparedWrite? prepared, out LatticeException? refusal)
    {
        StandingReserveDiagnostics.Observe?.Invoke("prepare", 0);
'@
    Update-Exact $coordinator @'
            _changes.GiveBackReserves();

            var preparation = TryPrepare(write, ref hold, out var prepared, out var refusal);
'@ @'
            _changes.GiveBackReserves();
            StandingReserveDiagnostics.Observe?.Invoke("isolated", 0);

            var preparation = TryPrepare(write, ref hold, out var prepared, out var refusal);
'@

    $arena = Join-Path $destination 'src/Moltaro.Lattice.Engine/Storage/Arena.cs'
    Update-Exact $arena @'
        if (_regionsWithFreeSlot[sizeClass] == NoRegion && !TryTakeRegion(sizeClass, reclaimCollectionSlots))
        {
'@ @'
        if (_regionsWithFreeSlot[sizeClass] == NoRegion && !TryTakeRegion(sizeClass, reclaimCollectionSlots))
        {
            global::Moltaro.Lattice.Engine.StandingReserveDiagnostics.Observe?.Invoke(
                kind == BlockKindEnum.SharedText ? "text-slot-refused" : "slot-refused", (ulong)SizeClasses.SizeOf(sizeClass));
'@

    $nodePool = Join-Path $engine 'Indexes/NodePool.cs'
    Update-Exact $nodePool '    public void GiveBackReserve() => Trim(keepsReserve: false);' @'
    public void GiveBackReserve()
    {
        var before = StandingReserveDiagnostics.Nodes(_arena, _head);
        Trim(keepsReserve: false);
        if (before is not null)
        {
            foreach (ulong address in before.Except(StandingReserveDiagnostics.Nodes(_arena, _head)!))
            {
                StandingReserveDiagnostics.Observe?.Invoke("returned", address);
            }
        }
    }
'@
    Update-Exact $nodePool @'
        MemoryMarshal.Write(_arena.Payload(node), in _head);
        _head = node;
        _count++;

        return true;
'@ @'
        MemoryMarshal.Write(_arena.Payload(node), in _head);
        _head = node;
        _count++;
        StandingReserveDiagnostics.Observe?.Invoke(reclaimCollectionSlots ? "required" : "top-up", node.Value);

        return true;
'@
    Update-Exact $nodePool @'
        _ledger.Taken++;
        _arena.Publish(node);
'@ @'
        _ledger.Taken++;
        _arena.Publish(node);
        StandingReserveDiagnostics.Observe?.Invoke("published", node.Value);
'@
}

# Both runnable copies use the same small driver entry. It does not require S24 symbols
# and leaves the existing measurement program in the source checkout untouched.
$entry = Join-Path $measurements 'Program.cs'
[IO.File]::WriteAllText($entry, @'
using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

if (args is [StandingReserveScenario.Verify, "development-5", var lane, var limit, var revision,
    var cleanHash, var receipt, var nonce])
{
    await StandingReserveScenario.RunAsync(StandingReserveScenario.Verify, lane,
        long.Parse(limit, CultureInfo.InvariantCulture), 0, revision, null,
        new StandingReserveTrace.Invocation(cleanHash, receipt, nonce));
    return 0;
}

if (args is [StandingReserveScenario.Preflight, "development-5", var probeLane, var probeCap, var probeSha, var probeDriver])
{
    foreach (var line in await StandingReserveScenario.RunAsync(StandingReserveScenario.Preflight, probeLane,
        long.Parse(probeCap, CultureInfo.InvariantCulture), 0, probeSha, probeDriver))
    {
        Console.WriteLine(line);
    }
    return 0;
}

if (args is [StandingReserveScenario.Name, "development-5", var timedLane, var cap, var round, var sha, var diagnostic] &&
    OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build == 26300 && Environment.Version == new Version(10, 0, 15))
{
    using var process = Process.GetCurrentProcess();
    if (process.ProcessorAffinity != 0xFF)
    {
        throw new InvalidOperationException("development-5 requires process affinity 0xFF.");
    }

    foreach (string line in await StandingReserveScenario.RunAsync(StandingReserveScenario.Name, timedLane,
        long.Parse(cap, CultureInfo.InvariantCulture), int.Parse(round, CultureInfo.InvariantCulture), sha, diagnostic))
    {
        Console.WriteLine(line);
    }
    return 0;
}

Console.Error.WriteLine("Use a pinned development-5 standing-reserve invocation.");
return 2;
'@)

Push-Location (Join-Path $destination 'src')
try {
    $dotnetVersion = (dotnet --version).Trim()
    if ($dotnetVersion -ne '10.0.404') {
        throw "development-5 requires .NET SDK 10.0.404, found $dotnetVersion."
    }

    dotnet build Moltaro.Lattice.Measurements/Moltaro.Lattice.Measurements.csproj -c Release -p:SourceRevisionId=$Revision --nologo
    if ($LASTEXITCODE -ne 0) { throw 'The comparison build failed.' }
}
finally {
    Pop-Location
}
