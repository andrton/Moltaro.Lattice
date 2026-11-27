using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Replays the reference master tape without opening a transport. Samples are at the writer's
// cycle boundaries; reads keep the reference master's original offsets within each burst.
internal static class BudgetChurnScenario
{
    public const string Plan = "s24-churn-plan";
    public const string Preflight = "s24-churn-preflight";
    public const string Control = "s24-churn-control";
    public const string Tight = "s24-churn-tight";

    private const string Tape = "cccb5b144ccfa3400f35e02bc06a410eb6c106d637f8484dd893aa9ad0e0f456";
    private const string Fixture = "f8794f7d00404b4984e84770d564d525364a5b180ae99f9e0e552a1313d96e6e";
    private const long NonBinding = long.MaxValue;
    private const int CycleFrames = 10 * ChurnTrace.FramesPerSecond;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<IReadOnlyList<string>> RunAsync(string mode, string? preflightPath)
    {
        var schema = LatticeSchema.Compile(ReferenceFixture.SchemaJson);
        var fixture = ReferenceFixture.Generate(schema);
        var trace = new ChurnTrace(smoke: false, FixtureRecords.Users, cyclic: true);
        ChurnAudit.Require(ReferenceFixture.Digest(fixture) == Fixture && trace.Digest == Tape,
            "The pinned S24 fixture or offered tape has changed.");
        if (mode == Plan)
        {
            return [$"Seed {ChurnTrace.Seed}, fixture {Fixture}, tape {Tape}.",
                $"{trace.WarmFrames} warm frames, {trace.MeasuredFrames} measured frames, {trace.FrameRate} frames/s.",
                $"{trace.Transactions} scheduled transactions including warm-up, on one master with standard maintenance."];
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(EngineDatabase).Assembly.Location)));
        var preset = mode == Tight ? ReadPreset(preflightPath!, hash) : null;
        long limit = preset?.Limit ?? NonBinding;
        string output = Path.Combine(".local", "measurements", $"{mode}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss.fffffff}");
        Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var stop = deadline.Token;
        var machine = await MeasurementEnvironment.CaptureAsync(stop);
        var ledger = new ChurnLedger();
        var turns = new ChurnTurns();
        Task turnFile = turns.WriteAsync(Path.Combine(output, "master-turns.bin"));
        var inventory = new BudgetInventory();
        var model = new BudgetChurnModel(trace);
        var audit = new ChurnAudit(fixture);
        var authoredVolume = ChurnVolume.Model(fixture, trace);
        var grantAdmissions = new List<GrantAdmission>();
        string[] report;
        try
        {
            await using var node = EngineDatabase.Create(new LatticeDatabaseOptions
            {
                Schema = schema,
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                MemoryLimit = limit,
            }, ChurnInputs.Create(ledger, off: false, turns));
            await node.OpenAsync(stop);

            ledger.BeforeGrant = (held, requested) =>
            {
                var reserved = node.InspectHeld();
                ChurnAudit.Require((long)reserved.GrantedSegments * reserved.SegmentBytes == held && reserved.Charged >= held + requested &&
                    reserved.Charged <= limit, "The independent allocator saw an unreserved native grant.");
                grantAdmissions.Add(new GrantAdmission(Stopwatch.GetTimestamp(), held, requested, reserved.Charged, reserved.Peak));
            };

            await SeedAsync(node, fixture, model, stop);
            var ready = inventory.Read("fixture-ready", 0, node, ledger, limit);
            CheckNative(ready);
            await WriteAsync(output, "ready.json", new
            {
                Inventory = ready,
                Machine = machine,
                Preset = preset,
                ReadyMinusPresetBytes = preset is null ? (long?)null : ready.IndependentBytes - preset.ReadyBytes
            }, stop);
            if (mode == Preflight)
            {
                var cap = new Preset(Fixture, Tape, hash, ready.IndependentBytes,
                    checked(ready.IndependentBytes + LatticeDatabaseOptions.ArenaSegmentSize));
                await WriteAsync(output, "tight-preset.json", cap, stop);
                report = [$"Ready inventory {ready.IndependentBytes:N0} B. Preset limit {cap.Limit:N0} B.",
                    $"Pass {Path.GetFullPath(Path.Combine(output, "tight-preset.json"))} to {Tight}."];
            }
            else
            {
                await File.WriteAllBytesAsync(Path.Combine(output, "trace.json"), JsonSerializer.SerializeToUtf8Bytes(trace.Frames), stop);
                var initialVolume = ChurnVolume.Observe(node, authoredVolume.Initial);
                using var boundary = new ChurnReadBoundary();
                var readers = new ChurnReads(boundary);
                readers.Start(node.Set("User"), child: null, trace);
                long interval = Stopwatch.Frequency / trace.FrameRate;
                long start = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                long plannedEnd = start + interval * trace.Frames.Length;
                readers.Arm(start, plannedEnd, interval);
                Replay replay;
                try
                {
                    replay = await Task.Factory.StartNew(() =>
                        Play(node, trace, model, audit, inventory, ledger, limit, readers, boundary, start, interval, stop),
                        stop, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch
                {
                    await deadline.CancelAsync();
                    throw;
                }
                finally
                {
                    await readers.FinishAsync(stop.IsCancellationRequested);
                }

                var actualRefs = model.CheckUsers(node);
                model.CheckCollections(node, audit, trace.CollectionExtra);
                var committedVolume = ChurnVolume.Model(fixture, trace,
                    frame => replay.Frames[frame].User.State == "changed",
                    frame => replay.Frames[frame].Collection.State == "changed");
                ChurnAudit.Require(ChurnVolume.Equivalent(replay.WarmVolume, committedVolume.MeasuredStart) &&
                    ChurnVolume.Equivalent(replay.TerminalVolume, committedVolume.Final),
                    "A public cycle-fence census disagreed with the independent committed-transaction model.");
                var finalVolume = replay.TerminalVolume;
                var warm = replay.Capacity.Single(sample => sample.Boundary == "measured-start");
                var last = replay.Capacity[^1];
                var calls = replay.Frames.Skip(trace.WarmFrames)
                    .SelectMany(frame => new[] { frame.User, frame.Collection }).Where(call => call.State is not null).ToArray();
                var dispositions = calls.GroupBy(call => call.State!).ToDictionary(group => group.Key, group => group.Count());
                int refused = dispositions.GetValueOrDefault("refused");
                int inapplicable = dispositions.GetValueOrDefault("inapplicable");
                int changed = dispositions.GetValueOrDefault("changed");
                if (mode == Control)
                {
                    ChurnAudit.Require(replay.Frames.All(frame => frame.User.State == "changed" &&
                        frame.Collection.State is null or "changed"), "The nonbinding run did not replay the complete authored tape.");
                    ChurnAudit.Require(ChurnVolume.Equivalent(replay.WarmVolume, authoredVolume.MeasuredStart) &&
                        ChurnVolume.Equivalent(finalVolume, authoredVolume.Final), "The control missed the reference live volume.");
                }

                CheckNative(warm);
                CheckNative(last);
                long peak = last.Budget.PeakBytes;
                ChurnAudit.Require(peak >= last.Native.PhasePeak, "The charged peak is below the independently observed native peak.");
                var exportedReaders = readers.Export(output);
                long writerSpill = Math.Max(0, replay.Frames[^1].Finished - replay.PlannedEnd);
                bool lateReaders = exportedReaders.Any(reader => reader.LastReadFromEndTicks > 0);
                string writerWindow = writerSpill > 0 ? "writer-extended" : "planned";
                var raw = new
                {
                    Mode = mode,
                    EngineSha256 = hash,
                    Fixture,
                    Tape,
                    Limit = limit,
                    Stopwatch.Frequency,
                    Frames = replay.Frames,
                    Capacity = replay.Capacity,
                    Machine = machine,
                    Preset = preset,
                    ReadyMinusPresetBytes = preset is null ? (long?)null : ready.IndependentBytes - preset.ReadyBytes,
                    WarmStart = start,
                    MeasurementStart = replay.MeasurementStart,
                    PlannedMeasuredEnd = replay.PlannedEnd,
                    TerminalWriterTicks = replay.Frames[^1].Finished,
                    TerminalInventoryTicks = last.Ticks,
                    WriterCompletion = replay.WriterCompletion,
                    AtOrAfterPlannedWall = replay.AtOrAfterPlannedWall,
                    WallObservationDelayTicks = replay.AtOrAfterPlannedWall.Ticks - replay.PlannedEnd,
                    TerminalReaderFenceWaitTicks = replay.ReaderFenceTicks,
                    LateReaderDrainInPeak = lateReaders,
                    WriterSpillTicks = writerSpill,
                    WriterFramesCompletedByWallEnd = replay.Frames.Count(frame => frame.Finished <= replay.PlannedEnd),
                    PeakIncludesWriterSpill = writerSpill > 0,
                    WriterWindow = writerWindow,
                    WarmCensusTicks = replay.WarmCensusTicks,
                    TerminalCensusTicks = replay.TerminalCensusTicks,
                    WarmWriterFrames = trace.WarmFrames,
                    TerminalWriterFrames = replay.Frames.Length,
                    WarmReaders = replay.WarmReaders,
                    TerminalReaders = replay.TerminalReaders,
                    InitialVolume = initialVolume,
                    WarmVolume = replay.WarmVolume,
                    FinalVolume = finalVolume,
                    ReferenceWarmVolume = authoredVolume.MeasuredStart,
                    ReferenceFinalVolume = authoredVolume.Final,
                    ModelUserChecks = actualRefs,
                    Position = node.Position.ToString(),
                    Readers = exportedReaders,
                    MeasuredCalls = new { Offered = calls.Length, Submitted = calls.Length - inapplicable, Accepted = changed, Changed = changed, Refused = refused, Inapplicable = inapplicable },
                    RefusalsByLimit = calls.Where(call => call.State == "refused").GroupBy(call => call.Reason!)
                        .ToDictionary(group => group.Key, group => group.Count()),
                    PeakBytes = peak,
                    PeakOverWarm = (double)peak / warm.IndependentBytes,
                    PeakOverEnd = (double)peak / last.IndependentBytes,
                    CombinedRuntimeDriverEngineCallerAllocationBytesPerSecond = (last.ProcessAllocatedBytes - warm.ProcessAllocatedBytes)
                        / Stopwatch.GetElapsedTime(warm.Ticks, last.Ticks).TotalSeconds,
                };
                await WriteAsync(output, "churn.json", raw, stop);
                string peakWindow = lateReaders ? $"{writerWindow} window and late reader drain" : $"{writerWindow} window";
                report = [$"Offered {calls.Length:N0}, accepted/changed {changed:N0}, refused {refused:N0}, inapplicable {inapplicable:N0} in the {writerWindow} window.",
                    $"Peak {peak:N0} B through the {peakWindow}. Warm steady {warm.IndependentBytes:N0} B ({raw.PeakOverWarm:F3}×). End steady {last.IndependentBytes:N0} B ({raw.PeakOverEnd:F3}×).",
                    mode == Tight && refused == 0 ? "No capacity refusal was observed in that window." : $"Raw outcomes with refusal reasons are in {output}."];
            }
        }
        finally
        {
            turns.Complete();
            await turnFile.WaitAsync(TimeSpan.FromSeconds(40));
        }

        var closed = ledger.Snapshot();
        ChurnAudit.Require(closed.Held == 0 && closed.Allocations != 0, "The independent ledger did not see all segment grants returned.");
        ChurnAudit.Require(turns.Lost == 0, "A maintenance phase sample was lost.");
        await WriteAsync(output, "native-admissions.json", grantAdmissions, stop);
        await WriteAsync(output, "closed-native.json", closed, stop);
        return [.. report, $"Raw output in {output}"];
    }

    private static Preset ReadPreset(string path, string engineHash)
    {
        var preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(path));
        ChurnAudit.Require(preset is not null && preset.Fixture == Fixture && preset.Tape == Tape &&
            preset.EngineSha256 == engineHash && preset.ReadyBytes > 0 &&
            preset.Limit == checked(preset.ReadyBytes + LatticeDatabaseOptions.ArenaSegmentSize),
            "The preset must come from the untimed Ready inventory of this engine, fixture and tape.");
        return preset!;
    }

    private static async Task SeedAsync(EngineDatabase node, IEnumerable<LatticeRecord> fixture, BudgetChurnModel model, CancellationToken token)
    {
        var sets = new[] { "User", "Group", "Role", "Setting" }.ToDictionary(name => name, node.Set);
        foreach (var row in fixture)
        {
            var write = await sets[row.EntityType.Name].InsertAsync(row, token);
            model.Seed(row, write);
        }
    }

    private static Replay Play(EngineDatabase node, ChurnTrace trace, BudgetChurnModel model, ChurnAudit audit,
        BudgetInventory inventory, ChurnLedger ledger, long limit,
        ChurnReads readers, ChurnReadBoundary boundary, long start, long interval, CancellationToken stop)
    {
        var snapshots = new List<BudgetInventory.Snapshot>();
        var frames = new FrameResult[trace.Frames.Length];
        var users = node.Set("User");
        long next = start;
        long measuredStart = 0;
        long warmCensusTicks = 0;
        ChurnVolume.Volume? warmVolume = null;
        ChurnReads.ReadFence[]? warmReaders = null;
        foreach (var frame in trace.Frames)
        {
            if (frame.Number == trace.WarmFrames)
            {
                ChurnPacing.WaitUntil(start + frame.Number * interval, stop);
                boundary.FinishWarm(stop);
                warmReaders = readers.Progress();
                ChurnAudit.Require(warmReaders.Length == 4 && warmReaders.All(reader => reader.CompletedBursts == trace.WarmFrames),
                    "The warm reader and writer fences did not match.");
                long census = Stopwatch.GetTimestamp();
                warmVolume = ChurnVolume.Observe(node);
                warmCensusTicks = Stopwatch.GetTimestamp() - census;
                node.WithOwnershipAudit(() =>
                {
                    snapshots.Add(inventory.Read("warm-lifetime-peak", frame.Number, node, ledger, limit));
                    ledger.StartPhase();
                    node.ResetBudgetPeak();
                    snapshots.Add(inventory.Read("measured-start", frame.Number, node, ledger, limit));
                });
                measuredStart = Stopwatch.GetTimestamp();
                next = measuredStart;
                boundary.Resume(measuredStart);
            }

            long due = frame.Number < trace.WarmFrames ? start + frame.Number * interval
                : measuredStart + (frame.Number - trace.WarmFrames) * interval;
            ChurnPacing.WaitUntil(Math.Max(due, next), stop);
            if (frame.Number % CycleFrames == 0 && frame.Number != trace.WarmFrames)
            {
                snapshots.Add(inventory.Read("cycle", frame.Number, node, ledger, limit));
            }

            long began = Stopwatch.GetTimestamp();
            next = began + interval / 2;
            var user = OfferUser(node, users, frame, model, stop);
            var collection = default(Outcome);
            switch (frame.Collection)
            {
                case ChurnTrace.CollectionChangeEnum.Grow or ChurnTrace.CollectionChangeEnum.Trim:
                    collection = OfferCollection(node, trace, frame, model, audit, stop);
                    break;
                case ChurnTrace.CollectionChangeEnum.CheckAfterHold:
                    model.CheckCollections(node, audit, trace.CollectionExtra);
                    break;
            }

            frames[frame.Number] = new FrameResult(frame.Number, due, began, Stopwatch.GetTimestamp(), user, collection);
        }

        var writerCompletion = ReadFence(node, readers);
        long end = measuredStart + interval * trace.MeasuredFrames;
        ChurnPacing.WaitUntil(end, stop);
        var atOrAfterPlannedWall = ReadFence(node, readers);
        long awaitingReaders = Stopwatch.GetTimestamp();
        readers.WaitForTerminal(stop);
        long readerFenceTicks = Stopwatch.GetTimestamp() - awaitingReaders;
        var terminalReaders = readers.Progress();
        ChurnAudit.Require(terminalReaders.Length == 4 && terminalReaders.All(reader => reader.CompletedBursts == frames.Length),
            "All original readers must finish their last burst before the terminal census.");
        snapshots.Add(inventory.Read("measured-end", frames.Length, node, ledger, limit));
        ChurnAudit.Require(node.Position.Sequence == model.Sequence, "The writer position disagreed with the committed model.");
        long terminalCensus = Stopwatch.GetTimestamp();
        var terminalVolume = ChurnVolume.Observe(node);
        return new Replay(frames, snapshots, measuredStart, end, warmCensusTicks, Stopwatch.GetTimestamp() - terminalCensus,
            warmVolume!, terminalVolume, warmReaders!, terminalReaders, writerCompletion, atOrAfterPlannedWall, readerFenceTicks);
    }

    private static ProgressFence ReadFence(EngineDatabase node, ChurnReads readers)
    {
        var progress = readers.Progress();
        var budget = node.InspectHeld();
        return new ProgressFence(Stopwatch.GetTimestamp(), node.Position.Sequence, budget.Peak, progress);
    }

    private static Outcome OfferUser(EngineDatabase node, ILatticeSet users, ChurnTrace.Frame frame,
        BudgetChurnModel model, CancellationToken stop)
    {
        var dependency = model.UserDependency(frame);
        if (dependency is not null)
        {
            return new Outcome("inapplicable", dependency, model.Sequence);
        }

        var row = ChurnTrace.User(users.EntityType, frame.Key, frame.Number + 100, frame.Size, frame.Region);
        try
        {
            ValueTask<LatticeWriteResult> operation;
            if (frame.Kind == ChurnTrace.ChangeEnum.Exchange)
            {
                var exchange = node.CreateWriteBatch();
                exchange.Delete("User", frame.OldKey);
                exchange.Insert(row);
                operation = exchange.CommitAsync(stop);
            }
            else
            {
                operation = users.ReplaceAsync(row, stop);
            }

            model.ApplyUser(frame, row, Wait(operation));
            return new Outcome("changed", null, model.Sequence);
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.WriteRejected)
        {
            return Refused(node, model, exception);
        }
    }

    private static Outcome OfferCollection(EngineDatabase node, ChurnTrace trace, ChurnTrace.Frame frame,
        BudgetChurnModel model, ChurnAudit audit, CancellationToken stop)
    {
        if (!model.CollectionApplies(frame))
        {
            return new Outcome("inapplicable", "a preceding collection grow or trim did not apply", model.Sequence);
        }

        try
        {
            model.ApplyCollection(frame, Wait(audit.EditCollectionsAsync(node, frame, trace.CollectionExtra, stop)));
            return new Outcome("changed", null, model.Sequence);
        }
        catch (LatticeException exception) when (exception.Error == LatticeErrorEnum.WriteRejected)
        {
            return Refused(node, model, exception);
        }
    }

    private static Outcome Refused(EngineDatabase node, BudgetChurnModel model, LatticeException exception)
    {
        ChurnAudit.Require(node.Position.Sequence == model.Sequence && exception.Limit.HasValue && exception.OperationIndex is null,
            "A refused transaction changed the position or failed the resource-refusal contract.");
        return new Outcome("refused", exception.Limit.ToString(), model.Sequence);
    }

    private static void CheckNative(BudgetInventory.Snapshot sample) =>
        ChurnAudit.Require(sample.Native.Held == sample.Budget.NativeBytes && sample.Native.Held > 0,
            "The populated fixture did not reconcile with nonzero native grants.");

    private static T Wait<T>(ValueTask<T> write) =>
        write.IsCompletedSuccessfully ? write.Result : write.AsTask().GetAwaiter().GetResult();

    private static Task WriteAsync<T>(string output, string file, T value, CancellationToken token) =>
        File.WriteAllBytesAsync(Path.Combine(output, file), JsonSerializer.SerializeToUtf8Bytes(value, Json), token);

    private readonly record struct GrantAdmission(long Ticks, long HeldBefore, int Requested, long ReservedCharge, long Peak);
    private sealed record Preset(string Fixture, string Tape, string EngineSha256, long ReadyBytes, long Limit);
    private sealed record Replay(FrameResult[] Frames, IReadOnlyList<BudgetInventory.Snapshot> Capacity,
        long MeasurementStart, long PlannedEnd, long WarmCensusTicks, long TerminalCensusTicks, ChurnVolume.Volume WarmVolume,
        ChurnVolume.Volume TerminalVolume, ChurnReads.ReadFence[] WarmReaders, ChurnReads.ReadFence[] TerminalReaders,
        ProgressFence WriterCompletion, ProgressFence AtOrAfterPlannedWall, long ReaderFenceTicks);
    private sealed record ProgressFence(long Ticks, ulong Position, long PeakBytes, ChurnReads.ReadFence[] Readers);
    private readonly record struct FrameResult(int Frame, long Due, long Started, long Finished, Outcome User, Outcome Collection);
    private readonly record struct Outcome(string? State, string? Reason, ulong Sequence);
}
