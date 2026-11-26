using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Only a diagnostic engine built from the same source can produce the untimed branch witness.
internal sealed class StandingReserveTrace : IDisposable
{
    private const string Diagnostics = "Moltaro.Lattice.Engine.StandingReserveDiagnostics";
    private readonly FieldInfo _observer;
    private readonly List<Event> _events = [];

    private StandingReserveTrace(FieldInfo observer) => _observer = observer;

    public static string EngineHash => Hash(typeof(EngineDatabase).Assembly.Location);

    public static void RequireClean(string revision)
    {
        var assembly = typeof(EngineDatabase).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        ChurnAudit.Require(version?.EndsWith(revision, StringComparison.OrdinalIgnoreCase) == true &&
            assembly.GetType(Diagnostics) is null, "The timing executable did not load the exact uninstrumented engine.");
    }

    public static StandingReserveTrace Install(string revision, string cleanEngineHash)
    {
        var type = typeof(EngineDatabase).Assembly.GetType(Diagnostics);
        ChurnAudit.Require(type is not null && Read(type, "Revision") == revision &&
            Read(type, "CleanEngineSha256") == cleanEngineHash && EngineHash != cleanEngineHash,
            "The diagnostic engine was not built for this comparison artifact.");
        var observer = type!.GetField("Observe", BindingFlags.Static | BindingFlags.Public)!;
        var trace = new StandingReserveTrace(observer);
        observer.SetValue(null, (Action<string, ulong>)trace.Record);
        return trace;
    }

    public static async Task<Receipt> RunAsync(string diagnosticDriver, string lane, long limit, string revision,
        string directory, CancellationToken token)
    {
        RequireClean(revision);
        var driver = Path.GetFullPath(diagnosticDriver);
        var engine = Path.Combine(Path.GetDirectoryName(driver)!, Path.GetFileName(typeof(EngineDatabase).Assembly.Location));
        string diagnosticHash = Hash(engine);
        string receipt = Path.GetFullPath(Path.Combine(directory, "branch-receipt.json"));
        string nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { driver, StandingReserveScenario.Verify, "development-5", lane,
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture), revision, EngineHash, receipt, nonce })
        {
            start.ArgumentList.Add(argument);
        }

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the pinned diagnostic driver.");
        var readError = child.StandardError.ReadToEndAsync(token);
        try
        {
            await child.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            throw;
        }

        string diagnosticError = await readError;
        if (diagnosticError.Length != 0)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "branch-stderr.txt"), diagnosticError, token);
        }

        if (child.ExitCode != 0 || !File.Exists(receipt))
        {
            throw new InvalidOperationException($"Diagnostic process exited with code {child.ExitCode}. Evidence in {Path.GetFullPath(directory)}.{Environment.NewLine}{diagnosticError.TrimEnd()}");
        }
        var report = JsonSerializer.Deserialize<Receipt>(await File.ReadAllBytesAsync(receipt, token))!;
        ChurnAudit.Require(report.Lane == lane && report.Limit == limit && report.Revision == revision &&
            report.Nonce == nonce && report.CleanEngineSha256 == EngineHash && report.DiagnosticEngineSha256 == diagnosticHash &&
            report.Witnesses.Length == 2, "The receipt did not come from the requested diagnostic artifact and invocation.");
        foreach (var witness in report.Witnesses)
        {
            Check(witness, lane.EndsWith("-tight", StringComparison.Ordinal), lane.StartsWith("six-", StringComparison.Ordinal), limit);
        }

        return report;
    }

    public Event[] Finish()
    {
        var events = _events.ToArray();
        _events.Clear();
        return events;
    }

    public static void Check(Witness witness, bool tight, bool six, long limit)
    {
        string[] expected = ["prepare"];
        if (tight)
        {
            expected = ["prepare", "text-slot-refused", "prepare", "text-slot-refused",
                "returned", "returned", "returned", "returned", "isolated", "prepare"];
            if (six)
            {
                expected = [.. expected, "text-slot-refused"];
            }
        }

        ChurnAudit.Require(witness.Before.EffectiveLimit == limit && witness.AfterUnindexed.EffectiveLimit == limit &&
            witness.AfterIndexed.EffectiveLimit == limit && witness.UnindexedChanged == !(six && tight) &&
            witness.UnindexedRefusal == (six && tight ? "Memory" : null),
            "The diagnostic did not exercise the effective limit and outcome of this lane.");
        ChurnAudit.Require(witness.Unindexed.Select(item => item.Kind).SequenceEqual(expected) &&
            witness.Unindexed.Where(item => item.Kind == "text-slot-refused").All(item => item.Address == 1_024),
            "An attempt did not follow the expected admission path with a real 1,024-byte native text-slot refusal.");

        // Preparation reserves for a possible split. The short case-only rewrite publishes one leaf.
        string[] indexed = tight
            ? ["prepare", "required", "required", "required", "top-up", "top-up", "published"]
            : ["prepare", "top-up", "published"];
        ChurnAudit.Require(witness.Indexed.Select(item => item.Kind).SequenceEqual(indexed),
            "The rewrite must reach the expected required allocation, optional top-up and single leaf publication.");
        var returned = witness.Unindexed.Where(item => item.Kind == "returned").Select(item => item.Address).ToArray();
        ChurnAudit.Require(tight ? returned.Order().SequenceEqual(witness.Before.Pool.Order()) && returned.Length == 4
            : returned.Length == 0, "The measured branch did not return the original standing nodes.");
        if (tight)
        {
            var required = witness.Indexed.Where(item => item.Kind == "required").Select(item => item.Address).ToArray();
            var optional = witness.Indexed.Where(item => item.Kind == "top-up").Select(item => item.Address).ToArray();
            ChurnAudit.Require(optional.Intersect(returned).Any() && !required.Intersect(optional).Any() &&
                witness.Before.Native == witness.AfterUnindexed.Native && witness.Before.Native == witness.AfterIndexed.Native,
                "The next write did not refill the optional reserve from returned slots without another grant.");
        }
    }

    public void Dispose() => _observer.SetValue(null, null);

    private void Record(string kind, ulong address) => _events.Add(new Event(kind, address));

    private static string Read(Type type, string name) =>
        (string)type.GetField(name, BindingFlags.Static | BindingFlags.Public)!.GetRawConstantValue()!;

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    internal sealed record Invocation(string CleanEngineSha256, string ReceiptPath, string Nonce);
    internal sealed record Receipt(string Lane, long Limit, string Revision, string CleanEngineSha256,
        string DiagnosticEngineSha256, string Nonce, Witness[] Witnesses);
    internal sealed record Witness(StandingReserveInventory.Snapshot Before, StandingReserveInventory.Snapshot AfterUnindexed,
        StandingReserveInventory.Snapshot AfterIndexed, Event[] Unindexed, Event[] Indexed,
        ulong BeforePosition, ulong UnindexedPosition, ulong IndexedPosition, string ProbeRevisionBefore, string ProbeRevisionAfter,
        string IndexRevisionBefore, string IndexRevisionAfter, bool UnindexedChanged, string? UnindexedRefusal);
    internal sealed record Event(string Kind, ulong Address);
}
