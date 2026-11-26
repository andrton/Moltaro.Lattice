using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal static class StandingReserveScenario
{
    public const string Plan = "s14-reserve-plan";
    public const string Preflight = "s14-reserve-preflight";
    public const string Verify = "s14-reserve-verify";
    public const string Name = "s14-standing-reserve";

    private const int WarmPairs = 5_000;
    private const int MeasuredPairs = 25_000;
    private const int TextBytes = 992;
    private const int NodeBytes = 1_024;
    private const int Seed = 6035;
    private const int Fillers = 52;
    private const int TextFillers = 3;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<IReadOnlyList<string>> RunAsync(string mode, string lane, long limit, int round, string revision, string? diagnosticDriver,
        StandingReserveTrace.Invocation? invocation = null)
    {
        bool tight = lane.EndsWith("-tight", StringComparison.Ordinal);
        bool six = lane.StartsWith("six-", StringComparison.Ordinal);
        ChurnAudit.Require(lane is "two-tight" or "six-tight" or "two-control" or "six-control", "Unknown reserve lane.");
        var schema = MakeSchema(six);
        var engineHash = StandingReserveTrace.EngineHash;
        if (mode == Plan)
        {
            return [$"Five AB/BA pairs, {WarmPairs:N0} warm and {MeasuredPairs:N0} measured pairs per lane.", "Lanes: two-tight, six-tight, two-control, six-control.",
                "Timed invocation takes the lane, limit, round, exact revision and the pinned diagnostic driver DLL. The paired launcher verifies all lanes before timing."];
        }

        string output = Path.Combine(".local", "measurements", $"{mode}-{lane}-{round}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss.fffffff}");
        Directory.CreateDirectory(output);
        var ledger = new ChurnLedger();
        var inventory = new StandingReserveInventory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stop = deadline.Token;
        var branches = mode is Name or Preflight
            ? await StandingReserveTrace.RunAsync(diagnosticDriver!, lane, limit, revision, output, stop)
            : null;
        if (mode == Preflight)
        {
            return [$"Untimed diagnostic receipt in {Path.GetFullPath(output)}."];
        }

        var machine = mode == Name ? await MeasurementEnvironment.CaptureAsync(stop) : null;
        using var diagnostic = mode == Verify ? StandingReserveTrace.Install(revision, invocation!.CleanEngineSha256) : null;
        StandingReserveInventory.Snapshot initial;
        StandingReserveInventory.Snapshot final;
        StandingReserveTrace.Witness[] witnesses;
        Call[] calls;
        await using (var node = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = schema,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        }, new EngineTestInputs
        {
            Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = limit, ReservedSegments = 0 },
            Allocator = ledger,
            IndexSeed = Seed,
            DisableBackgroundMaintenance = true,
        }))
        {
            await node.OpenAsync(stop);
            var indexed = node.Set("Indexed");
            var probe = node.Set("Probe");
            var filler = node.Set("Filler");
            var textFiller = six ? node.Set("TextFiller") : null;
            var lower = Indexed(indexed, "north");
            var upper = Indexed(indexed, "NORTH");
            var original = Probe(probe, first: 'a', rest: 'a');
            var replaced = Probe(probe, first: 'b', rest: six ? 'b' : 'a');
            var data = Enumerable.Repeat((byte)0x6c, TextBytes).ToArray();

            RequireChanged(await indexed.InsertAsync(lower, stop));
            RequireChanged(await probe.InsertAsync(original, stop));
            for (long id = 1; id <= Fillers; id++)
            {
                var set = filler;
                object payload = data;
                if (textFiller is not null && id <= TextFillers)
                {
                    set = textFiller;
                    payload = ("f" + id.ToString(CultureInfo.InvariantCulture)).PadRight(TextBytes, 'f');
                }

                var row = new LatticeRecord(set.EntityType) { ["Id"] = id, ["Payload"] = payload };
                RequireChanged(await set.InsertAsync(row, stop));
            }

            if (six)
            {
                PrepareTextCapacity(node);
            }

            RequireChanged(await indexed.ReplaceAsync(upper, stop));
            node.DrainRetired();
            var start = inventory.Read(node, ledger);
            if (invocation is not null)
            {
                await SaveDiagnosticAsync(invocation, "branch-start.json", start, stop);
            }

            if (tight)
            {
                ExpectTight(start);
            }

            async Task<StandingReserveTrace.Witness> WitnessAsync(int pair)
            {
                var before = inventory.Read(node, ledger);
                var state = State(node);
                _ = diagnostic!.Finish();
                var unindexed = await ReplaceAsync(probe, pair % 2 == 0 || six && tight ? replaced : original, stop);
                var unindexedEvents = diagnostic.Finish();
                var unindexedState = State(node);
                node.DrainRetired();
                var afterUnindexed = inventory.Read(node, ledger);
                var changed = await ReplaceAsync(indexed, pair % 2 == 0 ? lower : upper, stop);
                var indexedEvents = diagnostic.Finish();
                var indexedState = State(node);
                node.DrainRetired();
                var afterIndexed = inventory.Read(node, ledger);
                var witness = new StandingReserveTrace.Witness(before, afterUnindexed, afterIndexed, unindexedEvents, indexedEvents,
                    state.Position, unindexedState.Position, indexedState.Position, state.Probe.ToString(), unindexedState.Probe.ToString(),
                    state.Indexed.ToString(), indexedState.Indexed.ToString(), unindexed.Changed, unindexed.Refusal);
                await SaveDiagnosticAsync(invocation!, $"branch-observation-{pair + 1}.json", witness, stop);

                Expect(unindexed, refused: six && tight);
                Expect(changed, refused: false);
                if (six)
                {
                    long textBytes = start.ManagedBackingBytes["Text metadata"];
                    ChurnAudit.Require(before.ManagedBackingBytes["Text metadata"] == textBytes &&
                        afterUnindexed.ManagedBackingBytes["Text metadata"] == textBytes &&
                        afterIndexed.ManagedBackingBytes["Text metadata"] == textBytes,
                        "A six-text pair changed the prepared text metadata capacity.");
                }

                if (tight)
                {
                    ExpectTight(before);
                    ExpectTight(afterIndexed);
                    ChurnAudit.Require(afterUnindexed.Pool.Length == 0 && before.Pool.Intersect(afterIndexed.Pool).Any(),
                        "The unindexed write did not give back capacity that the next indexed rewrite retained.");
                    ChurnAudit.Require(before.Native == afterUnindexed.Native && before.Native == afterIndexed.Native,
                        "A tight pair obtained or returned a segment instead of reusing the full region.");
                }

                CheckRows(node, six && tight ? original : pair % 2 == 0 ? replaced : original, pair % 2 == 0 ? lower : upper);
                ChurnAudit.Require(unindexedState.Position == state.Position + (unindexed.Changed ? 1UL : 0UL) &&
                    (unindexedState.Probe != state.Probe) == unindexed.Changed && unindexedState.Indexed == state.Indexed &&
                    indexedState.Position == unindexedState.Position + 1 && indexedState.Probe == unindexedState.Probe &&
                    indexedState.Indexed != unindexedState.Indexed, "The diagnostic outcomes changed the wrong revisions or positions.");
                StandingReserveTrace.Check(witness, tight, six, limit);
                return witness;
            }

            if (mode == Verify)
            {
                witnesses = [await WitnessAsync(0), await WitnessAsync(1)];
                initial = start;
                final = witnesses[^1].AfterIndexed;
                calls = [];
            }
            else
            {
                // Branch verification runs separately with symmetric counters in the two source revisions.
                // The timed calls have no branch callbacks, inspections or drains inside their timings.
                var beforeWarm = node.Position.Sequence;
                for (int pair = 0; pair < WarmPairs; pair++)
                {
                    await RunPairAsync(pair);
                }

                ChurnAudit.Require(node.Position.Sequence - beforeWarm == (ulong)(six && tight ? WarmPairs : 2 * WarmPairs),
                    "Warm-up reached an unexpected committed position.");
                initial = inventory.Read(node, ledger);
                if (tight)
                {
                    ExpectTight(initial);
                }

                calls = new Call[2 * MeasuredPairs];
                var startPosition = node.Position.Sequence;
                for (int pair = 0; pair < MeasuredPairs; pair++)
                {
                    var (unindexed, indexedWrite) = await RunPairAsync(pair);
                    calls[2 * pair] = unindexed;
                    calls[2 * pair + 1] = indexedWrite;
                }

                ChurnAudit.Require(node.Position.Sequence - startPosition == (ulong)(six && tight ? MeasuredPairs : 2 * MeasuredPairs),
                    "Measured work reached an unexpected committed position.");
                final = inventory.Read(node, ledger);
                if (tight)
                {
                    ExpectTight(final);
                    ChurnAudit.Require(initial.Native == final.Native, "A tight run used additional native grants.");
                }

                CheckRows(node, original, upper);
                witnesses = [];
            }

            async ValueTask<(Call Unindexed, Call Indexed)> RunPairAsync(int pair)
            {
                bool even = pair % 2 == 0;
                var unindexed = await ReplaceAsync(probe, even || six && tight ? replaced : original, stop);
                Expect(unindexed, refused: six && tight);
                node.DrainRetired();
                var indexedWrite = await ReplaceAsync(indexed, even ? lower : upper, stop);
                Expect(indexedWrite, refused: false);
                node.DrainRetired();
                return (unindexed, indexedWrite);
            }
        }

        var closed = ledger.Snapshot();
        ChurnAudit.Require(closed.Held == 0 && closed.Allocations != 0, "Independent native grants did not reconcile at close.");
        if (invocation is not null)
        {
            var receipt = new StandingReserveTrace.Receipt(lane, limit, revision, invocation.CleanEngineSha256,
                engineHash, invocation.Nonce, witnesses);
            await using var receiptFile = new FileStream(invocation.ReceiptPath, FileMode.CreateNew);
            await JsonSerializer.SerializeAsync(receiptFile, receipt, Json, stop);
        }

        var distributions = new[] { "unindexed", "indexed" }.SelectMany((operation, kind) =>
            calls.Where((_, place) => place % 2 == kind).GroupBy(call => call.Refusal ?? (call.Changed ? "changed" : "unchanged"))
                .Select(outcome => Summarize(operation, outcome.Key, outcome))).ToArray();
        var raw = new
        {
            Mode = mode,
            Lane = lane,
            Limit = limit,
            Round = round,
            Revision = revision,
            EngineSha256 = engineHash,
            Machine = machine,
            Stopwatch.Frequency,
            Initial = initial,
            Final = final,
            Closed = closed,
            WarmPairs,
            MeasuredPairs,
            Calls = calls,
            Witnesses = witnesses,
            Branches = branches,
            Distributions = distributions,
        };
        await File.WriteAllBytesAsync(Path.Combine(output, "reserve.json"), JsonSerializer.SerializeToUtf8Bytes(raw, Json), stop);
        return [$"Lane {lane}, round {round}, engine {revision}.",
            .. distributions.Select(group => $"{group.Name} {group.Outcome}, {group.Offered:N0} offered, {group.Accepted:N0} accepted, {group.Refused:N0} refused, {group.Completed:N0} completed. p50/p95/p99/max ticks {group.P50}/{group.P95}/{group.P99}/{group.Max}."),
            $"Raw output in {output}."];
    }

    private static Task SaveDiagnosticAsync<T>(StandingReserveTrace.Invocation invocation, string name, T sample, CancellationToken token)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(invocation.ReceiptPath))!;
        return File.WriteAllBytesAsync(Path.Combine(directory, name), JsonSerializer.SerializeToUtf8Bytes(sample, Json), token);
    }

    private static async ValueTask<Call> ReplaceAsync(ILatticeSet set, LatticeRecord value, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            var result = await set.ReplaceAsync(value, token);
            return new Call(Stopwatch.GetTimestamp() - start, result.Changed, null, result.Position.Sequence);
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.WriteRejected)
        {
            return new Call(Stopwatch.GetTimestamp() - start, false, exception.Limit?.ToString(), null);
        }
    }

    private static void PrepareTextCapacity(EngineDatabase node)
    {
        // Nine resident dictionary entries keep space for sixteen through both outcomes. The
        // seed completes the bucket migration before any of the pairs that use that space.
        ChurnAudit.Require(node.TryRunMaintenanceStep(parts =>
        {
            var texts = parts.Arena.Texts;
            texts.Maintain(64);
            var shape = texts.MeasureCapacity();
            ChurnAudit.Require(texts.Count == 9 && shape.OldBuckets.Count == 0 &&
                shape.LocatorLeaves.Single(item => item.ArrayElementBytes == 8).ArrayLength == 16 &&
                shape.WriterMetadata.Single(item => item.ArrayElementBytes == 32).ArrayLength == 20 &&
                shape.ActiveBuckets.Single(item => item.ArrayElementBytes == 4).ArrayLength == 4,
                "The six-text seed did not retain the stable capacity for a complete preparation.");
        }), "The six-text seed was not quiescent.");
    }

    private static void ExpectTight(StandingReserveInventory.Snapshot sample)
    {
        var region = sample.Regions.Single(region => region.SlotBytes == NodeBytes);
        ChurnAudit.Require(sample.LiveIndexNodes == 1 && sample.StandingTarget == 5 && sample.Pool.Length == 4 &&
            region.Allocated == 63 && region.Live == 59 && sample.Arena.RetiredBytes == 0 &&
            sample.Arena.FreeRegions == 0 && sample.Arena.ReservedSegments == 0,
            "The tight lane did not start with four reserve nodes and one physical vacancy beside live slots.");
    }

    private static void CheckRows(EngineDatabase node, LatticeRecord expectedProbe, LatticeRecord expectedIndex)
    {
        var probe = node.Set("Probe").Get(1L)!;
        for (int text = 0; text < 6; text++)
        {
            string field = $"Text{text}";
            ChurnAudit.Require(probe.Get<string>(field) == expectedProbe.Get<string>(field), "The probe's six values differ from its committed result.");
        }

        var indexed = node.Set("Indexed");
        string expected = expectedIndex.Get<string>("Word")!;
        ChurnAudit.Require(indexed.Get(1L)!.Get<string>("Word") == expected &&
            indexed.Index("ByWord").Find(IndexKey.Equal("north")).Single().Get<string>("Word") == expected,
            "The indexed case-only rewrite did not publish the expected spelling.");
    }

    private static (ulong Position, LatticeRevision Probe, LatticeRevision Indexed) State(EngineDatabase node)
    {
        ChurnAudit.Require(node.Set("Probe").TryGet(1L, out _, out var probe), "The probe row disappeared.");
        ChurnAudit.Require(node.Set("Indexed").TryGet(1L, out _, out var indexed), "The indexed row disappeared.");
        return (node.Position.Sequence, probe, indexed);
    }

    private static void Expect(Call call, bool refused)
    {
        ChurnAudit.Require(refused ? call is { Changed: false, Refusal: "Memory", Sequence: null }
            : call is { Changed: true, Refusal: null, Sequence: not null }, "A reserve lane returned an unexpected outcome.");
    }

    private static void RequireChanged(LatticeWriteResult write) => ChurnAudit.Require(write.Changed, "Could not seed the reserve fixture.");

    private static LatticeRecord Indexed(ILatticeSet set, string word) => new(set.EntityType) { ["Id"] = 1L, ["Word"] = word };

    private static LatticeRecord Probe(ILatticeSet set, char first, char rest)
    {
        var row = new LatticeRecord(set.EntityType) { ["Id"] = 1L };
        for (int field = 0; field < 6; field++)
        {
            char variant = field < 2 ? first : rest;
            row[$"Text{field}"] = (variant + field.ToString(CultureInfo.InvariantCulture)).PadRight(TextBytes, 't');
        }

        return row;
    }

    private static LatticeSchema MakeSchema(bool six)
    {
        var fields = string.Concat(Enumerable.Range(0, 6).Select(field =>
            $$""",{"Id":{{field + 2}},"Name":"Text{{field}}","Type":"String","StringStorage":"Dictionary"}"""));
        string textFiller = six
            ? """,{"Id":4,"Name":"TextFiller","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Payload","Type":"String","StringStorage":"Dictionary"}],"PrimaryKey":[1]}"""
            : "";
        return LatticeSchema.Compile($$"""
            {"StringEncoding":"Utf8","EntityTypes":[
              {"Id":1,"Name":"Indexed","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Word","Type":"String","Comparison":"OrdinalIgnoreCase"}],
                "PrimaryKey":[1],"Indexes":[{"Id":1,"Name":"ByWord","Fields":[2]}]},
              {"Id":2,"Name":"Probe","Fields":[{"Id":1,"Name":"Id","Type":"Int64"}{{fields}}],"PrimaryKey":[1]},
              {"Id":3,"Name":"Filler","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Payload","Type":"Binary"}],"PrimaryKey":[1]}{{textFiller}}
            ]}
            """);
    }

    private static Distribution Summarize(string operation, string outcome, IEnumerable<Call> samples)
    {
        var calls = samples.ToArray();
        var ticks = calls.Select(call => call.Ticks).Order().ToArray();
        long Percentile(double value) => ticks.Length == 0 ? 0 : ticks[(int)Math.Ceiling(ticks.Length * value) - 1];
        int admitted = calls.Count(call => call.Refusal is null);
        return new Distribution(operation, outcome, calls.Length, admitted, calls.Length - admitted, calls.Length,
            Percentile(.5), Percentile(.95), Percentile(.99), Percentile(1));
    }

    private sealed record Distribution(string Name, string Outcome, int Offered, int Accepted, int Refused, int Completed, long P50, long P95, long P99, long Max);
    private readonly record struct Call(long Ticks, bool Changed, string? Refusal, ulong? Sequence);
}
