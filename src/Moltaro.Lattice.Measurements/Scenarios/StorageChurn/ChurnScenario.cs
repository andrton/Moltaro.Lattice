using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal static class ChurnScenario
{
    public const string Name = "storage-churn";
    public const string OffName = "storage-churn-off";
    private const string FullDigest = "f8794f7d00404b4984e84770d564d525364a5b180ae99f9e0e552a1313d96e6e";
    private static readonly TimeSpan ReplicationBound = TimeSpan.FromSeconds(40);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // The usual measurement entry point writes a fresh raw directory beside its normal report.
    public static Task<IReadOnlyList<string>> RunAsync(bool maintenance)
    {
        var scenario = maintenance ? Name : OffName;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss.fffffff", CultureInfo.InvariantCulture);
        var output = Path.Combine(".local", "measurements", $"{scenario}-{timestamp}");
        return RunAsync(maintenance ? "on" : "off", output);
    }

    // The evidence runner can add quiet, position-fenced inventory without changing the workload.
    internal static async Task<IReadOnlyList<string>> RunAsync(string mode, string output,
        Func<string, string, EngineDatabase, CancellationToken, Task<object>>? inventory = null)
    {
        if (mode is not ("on" or "off" or "quick-on" or "quick-off" or "smoke" or "cyclic-smoke" or
            "exploratory-on" or "exploratory-off"))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        bool smoke = mode == "smoke";
        bool quick = mode is "quick-on" or "quick-off";
        bool cyclicSmoke = mode == "cyclic-smoke";
        bool cyclic = cyclicSmoke || mode is "on" or "off";
        bool reduced = smoke || quick || cyclicSmoke;
        bool off = smoke || mode is "off" or "quick-off" or "exploratory-off";

        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("Output must be a new path.");
        }

        Directory.CreateDirectory(output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes((smoke || cyclicSmoke) ? 1 : quick ? 3 : 12));
        var stop = deadline.Token;
        var ledgerMaster = new ChurnLedger();
        var ledgerChild = new ChurnLedger();
        var masterTurns = new ChurnTurns();
        var childTurns = new ChurnTurns();
        Task writeMaster = masterTurns.WriteAsync(Path.Combine(output, "master-turns.bin"));
        Task writeChild = childTurns.WriteAsync(Path.Combine(output, "child-turns.bin"));
        var initialGc = ReadGc();
        var schema = LatticeSchema.Compile(ReferenceFixture.SchemaJson);
        var fixture = ReferenceFixture.Generate(schema);
        string fixtureDigest = ReferenceFixture.Digest(fixture);
        ChurnAudit.Require(fixtureDigest == FullDigest, "The pinned reference fixture digest changed.");
        var audit = new ChurnAudit(fixture);
        int users = smoke ? ChurnTrace.UsersInSmoke : (quick || cyclicSmoke) ? 12_288 : FixtureRecords.Users;
        var trace = new ChurnTrace(smoke, users, quick, cyclic, cyclicSmoke);
        var seed = SeedRecords(fixture, users, reduced);
        var volume = cyclic ? ChurnVolume.Model(seed, trace) : null;
        object? publicVolume = null;
        await File.WriteAllBytesAsync(Path.Combine(output, "trace.json"), JsonSerializer.SerializeToUtf8Bytes(trace.Frames), stop);
        var snapshots = new ConcurrentQueue<Snapshot>();
        var managed = new List<object>();
        var actor = new Actor();
        ChurnReads.Result[] readers;
        Phase phase;
        Dictionary<string, int> initialLive;
        Dictionary<string, int> finalLive;
        int finalAuditUsers;
        long startTicks;
        long measuredTicks;
        long endTicks;
        long openedMasterTicks;
        long openedChildTicks;
        int initialCount;
        object dictionaryAudit;
        try
        {
            await using var master = EngineDatabase.Create(new LatticeDatabaseOptions
            {
                Schema = schema,
                ListenUri = new Uri("lattice://127.0.0.1:0"),
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            }, ChurnInputs.Create(ledgerMaster, off, masterTurns));
            await master.OpenAsync(stop);
            openedMasterTicks = Stopwatch.GetTimestamp();

            initialCount = await SeedAsync(master, seed, stop);
            Console.Error.WriteLine($"loaded {initialCount} pinned fixture rows");
            await using var child = EngineDatabase.Create(new LatticeDatabaseOptions
            {
                Role = LatticeRoleEnum.Child,
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                MasterUri = master.ListenUri,
            }, ChurnInputs.Create(ledgerChild, off, childTurns));
            await child.OpenAsync(stop);
            openedChildTicks = Stopwatch.GetTimestamp();
            await child.WaitForPositionAsync(master.Position, ReplicationBound, stop);
            initialLive = LiveRows(master);
            CompareRows(initialLive, LiveRows(child), initialCount);
            await audit.CheckHeldRevisionsAsync(master, child, stop);
            audit.CheckCollections(master, 0);
            audit.CheckCollections(child, 0);
            var roles = new[] { new Node("master", master, ledgerMaster), new Node("child", child, ledgerChild) };
            var initialMasterVolume = volume is null ? null : ChurnVolume.Observe(master, volume.Initial);
            var initialChildVolume = volume is null ? null : ChurnVolume.Observe(child, volume.Initial);
            RecordSnapshot("ready", roles, 0, snapshots);
            if (inventory is not null)
            {
                managed.Add(await inventory("ready", "master", master, stop));
                managed.Add(await inventory("ready", "child", child, stop));
            }

            var reads = new ChurnReads();
            reads.Start(master.Set("User"), child.Set("User"), trace);
            long interval = Stopwatch.Frequency / trace.FrameRate;
            startTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            measuredTicks = startTicks + trace.WarmFrames * interval;
            endTicks = measuredTicks + trace.MeasuredFrames * interval;
            reads.Arm(startTicks, endTicks, interval);
            var checks = new List<Task>();
            Task sampleMemory = MonitorMemoryAsync(roles, actor, measuredTicks, endTicks, snapshots, smoke || cyclicSmoke, stop);
            try
            {
                phase = await Task.Factory.StartNew(() =>
                    Churn(master, child, audit, trace, actor, startTicks, measuredTicks, endTicks, interval,
                        roles, checks, snapshots, output, stop), stop, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await UntilAsync(endTicks, stop);
                RecordSnapshot("measured-end", roles, actor.CompletedFrames, snapshots);
            }
            catch
            {
                deadline.Cancel();
                throw;
            }
            finally
            {
                await reads.FinishAsync(stop.IsCancellationRequested);
                try
                {
                    await sampleMemory;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
            }

            await Task.WhenAll(checks).WaitAsync(ReplicationBound, stop);
            readers = reads.Export(output);
            await child.WaitForPositionAsync(master.Position, ReplicationBound, stop);
            RecordSnapshot("applied-fence", roles, actor.CompletedFrames, snapshots);
            if (inventory is not null)
            {
                managed.Add(await inventory("applied-fence", "master", master, stop));
                managed.Add(await inventory("applied-fence", "child", child, stop));
            }

            finalLive = LiveRows(master);
            CompareRows(initialLive, finalLive, initialCount);
            CompareRows(finalLive, LiveRows(child), initialCount);
            finalAuditUsers = audit.CheckFinalUsers(master, child, trace);
            int extra = trace.Frames.LastOrDefault(frame => frame.CollectionTarget == 2 &&
                frame.Collection is ChurnTrace.CollectionChangeEnum.Grow or ChurnTrace.CollectionChangeEnum.Trim)?.Collection == ChurnTrace.CollectionChangeEnum.Grow
                ? trace.CollectionExtra : 0;
            audit.CheckCollections(master, extra);
            audit.CheckCollections(child, extra);
            if (volume is not null)
            {
                publicVolume = new
                {
                    Expected = volume,
                    ObservedQuietInitial = new { Master = initialMasterVolume, Child = initialChildVolume },
                    ObservedQuietFinal = new
                    {
                        Master = ChurnVolume.Observe(master, volume.Final),
                        Child = ChurnVolume.Observe(child, volume.Final),
                    },
                };
            }

            dictionaryAudit = new { Master = AuditText(master), Child = AuditText(child) };
            RecordSnapshot("pre-close", roles, actor.CompletedFrames, snapshots);
        }
        finally
        {
            // The database's close must finish its maintenance participant before a measurement drain can complete.
            masterTurns.Complete();
            childTurns.Complete();
            await Task.WhenAll(writeMaster, writeChild).WaitAsync(TimeSpan.FromSeconds(40));
        }

        var masterClosed = ledgerMaster.Snapshot();
        var childClosed = ledgerChild.Snapshot();
        ChurnAudit.Require(masterClosed.Held == 0 && childClosed.Held == 0, "Native arenas are still held after close.");
        ChurnAudit.Require(masterTurns.Lost == 0 && childTurns.Lost == 0, "A maintenance sample was lost.");
        var status = new
        {
            Mode = mode,
            TurnTraceVersion = ChurnTurns.TraceVersion,
            Evidence = smoke ? "trace-and-oracle-only" : (quick || cyclicSmoke) ? "short-automatic-pipeline" : "automatic-background",
            Profile = cyclic ? "cyclic" : "exploratory",
            EngineSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(typeof(EngineDatabase).Assembly.Location))),
            CoreSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(typeof(LatticeRevision).Assembly.Location))),
            InitialGc = initialGc,
            FinalGc = ReadGc(),
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Processors = Environment.ProcessorCount,
            ClockFrequency = Stopwatch.Frequency,
            OpenedMasterTicks = openedMasterTicks,
            OpenedChildTicks = openedChildTicks,
            WarmStartTicks = startTicks,
            MeasuredStartTicks = measuredTicks,
            EndTicks = endTicks,
            FixtureDigest = fixtureDigest,
            FixtureRows = initialCount,
            TraceSeed = ChurnTrace.Seed,
            TraceDigest = trace.Digest,
            ReaderBurst = trace.ReadBurst,
            trace.FrameRate,
            trace.WarmFrames,
            trace.MeasuredFrames,
            trace.Transactions,
            trace.UserWrites,
            trace.CollectionWrites,
            Actual = phase,
            InitialLive = initialLive,
            FinalLive = finalLive,
            FinalAuditedUsers = finalAuditUsers,
            HeldRevisionsVerified = true,
            DictionaryAudit = dictionaryAudit,
            PublicVolume = publicVolume,
            Readers = readers,
            Snapshots = snapshots.ToArray(),
            ManagedStorage = managed,
            MasterTurns = masterTurns.Offered,
            ChildTurns = childTurns.Offered,
            MasterClosed = masterClosed,
            ChildClosed = childClosed,
        };
        await using var file = new FileStream(Path.Combine(output, "run.json"), FileMode.CreateNew);
        await JsonSerializer.SerializeAsync(file, status, Json, stop);
        List<string> results =
        [
            $"Storage churn {mode} with {initialCount} fixture rows and trace {trace.Digest}",
            $"{phase.Transactions} transactions, {phase.LateStarts} late writer starts, {phase.CrossedFrameBoundary} crossed frame boundaries",
            .. ChurnReport.Timings(output, readers, measuredTicks, endTicks)
        ];
        if (volume is not null)
        {
            var boundary = volume.MeasuredStart;
            results.Add($"Modeled logical volume across the measured window, {boundary.Rows} rows, " +
                $"{boundary.Utf8Bytes} UTF-8 bytes and {boundary.ScalarBytes} scalar bytes");
        }

        foreach (var role in new[] { "master", "child" })
        {
            var first = snapshots.Single(snapshot => snapshot.Mark == "measured-start" && snapshot.Role == role).Native;
            var last = snapshots.Single(snapshot => snapshot.Mark == "measured-end" && snapshot.Role == role).Native;
            results.Add($"{role} native capacity in bytes, initial {first.Held}, peak {last.PhasePeak}, final {last.Held}, " +
                $"returned {last.Returns - first.Returns} segments ({last.ReturnedBytes - first.ReturnedBytes} bytes)");
        }

        results.Add($"Raw results in {output}");
        return results;
    }

    private static IEnumerable<LatticeRecord> SeedRecords(IEnumerable<LatticeRecord> source, int userCount, bool reduced)
    {
        var counts = new Dictionary<string, int>();
        foreach (var record in source)
        {
            string type = record.EntityType.Name;
            int max = type == "User" ? userCount : 128;
            if (reduced && counts.GetValueOrDefault(type) == max)
            {
                continue;
            }

            counts[type] = counts.GetValueOrDefault(type) + 1;
            yield return record;
        }
    }

    private static async Task<int> SeedAsync(EngineDatabase master, IEnumerable<LatticeRecord> source, CancellationToken stop)
    {
        var sets = new[] { "User", "Group", "Role", "Setting" }.ToDictionary(name => name, master.Set);
        int count = 0;
        foreach (var record in source)
        {
            ChurnAudit.Require((await sets[record.EntityType.Name].InsertAsync(record, stop)).Changed,
                "An existing fixture record did not insert.");
            count++;
        }

        return count;
    }

    private static Phase Churn(EngineDatabase master, EngineDatabase child, ChurnAudit audit, ChurnTrace trace,
        Actor actor, long start, long measured, long end, long interval, Node[] roles, List<Task> checks,
        ConcurrentQueue<Snapshot> snapshots, string output, CancellationToken stop)
    {
        var set = master.Set("User");
        long lateness = 0;
        int lateStarts = 0;
        int crossed = 0;
        int userWrites = 0;
        int collectionWrites = 0;
        long nextStart = start;
        var timeline = new FrameTiming[trace.Frames.Length];

        foreach (var frame in trace.Frames)
        {
            long due = start + frame.Number * interval;
            ChurnPacing.WaitUntil(Math.Max(due, nextStart), stop);
            long actualStart = Stopwatch.GetTimestamp();
            nextStart = actualStart + interval / 2;
            long offset = actualStart - due;
            lateness = Math.Max(lateness, offset);
            lateStarts += offset * 2 > interval ? 1 : 0;

            if (frame.Number == trace.WarmFrames)
            {
                foreach (var role in roles)
                {
                    role.Ledger.StartPhase();
                }

                RecordSnapshot("measured-start", roles, actor.CompletedFrames, snapshots);
            }

            var user = ChurnTrace.User(set.EntityType, frame.Key, frame.Number + 100, frame.Size, frame.Region);
            if (frame.Kind == ChurnTrace.ChangeEnum.Exchange)
            {
                var exchange = master.CreateWriteBatch();
                exchange.Delete("User", frame.OldKey);
                exchange.Insert(user);
                ChurnAudit.Require(Wait(exchange.CommitAsync(stop)).Changed, "A planned exchange did not publish.");
                userWrites += 2;
            }
            else
            {
                ChurnAudit.Require(Wait(set.ReplaceAsync(user, stop)).Changed, "A planned replacement did not publish.");
                userWrites++;
            }

            if (frame.Collection is ChurnTrace.CollectionChangeEnum.Grow or ChurnTrace.CollectionChangeEnum.Trim)
            {
                Wait(audit.EditCollectionsAsync(master, frame, trace.CollectionExtra, stop));
                collectionWrites++;
            }

            if (frame.Collection == ChurnTrace.CollectionChangeEnum.CheckAfterHold)
            {
                var position = master.Position;
                const int extra = 0;
                checks.Add(Task.Run(async () =>
                {
                    await child.WaitForPositionAsync(position, ReplicationBound, stop);
                    audit.CheckCollections(master, extra);
                    audit.CheckCollections(child, extra);
                }, stop));
            }

            if (frame.Number >= trace.WarmFrames && (frame.Number + 1 - trace.WarmFrames) % 1000 == 0)
            {
                RecordSnapshot("operation-count", roles, frame.Number + 1, snapshots);
            }

            long done = Stopwatch.GetTimestamp();
            crossed += done >= Math.Min(end, due + interval) ? 1 : 0;
            timeline[frame.Number] = new FrameTiming(due, actualStart, done);
            actor.CompleteFrame(frame.Number + 1);
        }

        ChurnAudit.Require(userWrites == trace.UserWrites && collectionWrites == trace.CollectionWrites &&
            actor.CompletedFrames == trace.Frames.Length, "The completed trace diverged from its authored count.");
        using (var stream = new FileStream(Path.Combine(output, "writer.bin"), FileMode.CreateNew))
        using (var writer = new BinaryWriter(stream))
        {
            foreach (var item in timeline)
            {
                writer.Write(item.Due);
                writer.Write(item.Start);
                writer.Write(item.Done);
            }
        }

        return new Phase(trace.Frames.Length, userWrites, collectionWrites,
            userWrites - trace.Frames.Count(frame => frame.Kind == ChurnTrace.ChangeEnum.Exchange) + collectionWrites,
            lateStarts, crossed, lateness, timeline[^1].Done - measured);
    }

    private static async Task MonitorMemoryAsync(Node[] roles, Actor actor, long measured, long end,
        ConcurrentQueue<Snapshot> output, bool smoke, CancellationToken stop)
    {
        long period = Stopwatch.Frequency * (smoke ? 1 : 5);
        for (long next = measured + period; next < end; next += period)
        {
            await UntilAsync(next, stop);
            RecordSnapshot("elapsed", roles, actor.CompletedFrames, output);
            long seconds = (next - measured) / Stopwatch.Frequency;
            if (!smoke && seconds % 60 == 0)
            {
                Console.Error.WriteLine($"measured {seconds} s with {actor.CompletedFrames} completed frames");
            }
        }
    }

    private static void RecordSnapshot(string mark, Node[] nodes, int frames, ConcurrentQueue<Snapshot> output)
    {
        long at = Stopwatch.GetTimestamp();
        foreach (var node in nodes)
        {
            for (int attempt = 0; ; attempt++)
            {
                var before = node.Ledger.Snapshot();
                var counters = node.Database.Counters;
                var after = node.Ledger.Snapshot();
                if (before == after)
                {
                    ChurnAudit.Require(after.Held == (long)counters.Arena.Segments * LatticeDatabaseOptions.ArenaSegmentSize,
                        "Allocator retained bytes differ from engine segment count.");
                    output.Enqueue(new Snapshot(at, mark, node.Role, frames, after, counters, ReadGc()));
                    break;
                }

                ChurnAudit.Require(attempt < 7, "Native snapshots never landed between allocator events.");
            }
        }
    }

    private static Dictionary<string, int> LiveRows(EngineDatabase node)
    {
        var counts = new Dictionary<string, int>();
        node.ForEachLiveRoot((_, layout, _, _) =>
        {
            string name = layout.EntityType.Name;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        });
        return counts;
    }

    private static void CompareRows(Dictionary<string, int> expected, Dictionary<string, int> seen, int total) =>
        ChurnAudit.Require(expected.Count == seen.Count && seen.Values.Sum() == total &&
            expected.All(pair => seen.GetValueOrDefault(pair.Key) == pair.Value), "Logical entity count/composition changed.");

    private static object AuditText(EngineDatabase node)
    {
        var samples = node.TextReferences();
        var unique = samples.Select(sample => Convert.ToHexString(sample.Text)).ToHashSet();
        ChurnAudit.Require(unique.Count == samples.Count && samples.All(sample => sample.References > 0),
            "Text table repeated a live text or had a nonpositive reference count.");
        return new { Entries = samples.Count, References = samples.Sum(sample => sample.References), EncodedBytes = samples.Sum(sample => (long)sample.Text.Length) };
    }

    private static T Wait<T>(ValueTask<T> operation) =>
        operation.IsCompletedSuccessfully ? operation.Result : operation.AsTask().GetAwaiter().GetResult();

    private static void Wait(ValueTask operation)
    {
        if (operation.IsCompletedSuccessfully)
        {
            operation.GetAwaiter().GetResult();
        }
        else
        {
            operation.AsTask().GetAwaiter().GetResult();
        }
    }

    private static async ValueTask UntilAsync(long due, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long remaining = due - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            var milliseconds = (double)remaining * 1000 / Stopwatch.Frequency;
            if (milliseconds >= 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds - 1), token);
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    private static GcState ReadGc() =>
        new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().Ticks);

    private sealed record GcState(int Gen0, int Gen1, int Gen2, long ProcessGcPauseTimeSpanTicks);
    private sealed record Node(string Role, EngineDatabase Database, ChurnLedger Ledger);
    private sealed record Snapshot(long Ticks, string Mark, string Role, int CompletedFrames, ChurnLedger.Totals Native, EngineCounters Engine, GcState Gc);
    private readonly record struct FrameTiming(long Due, long Start, long Done);
    private sealed record Phase(int Frames, int UserWrites, int CollectionWrites, int Transactions, int LateStarts,
        int CrossedFrameBoundary, long MaximumStartLatenessTicks, long LastFrameDoneFromMeasuredStart);

    private sealed class Actor
    {
        private int _frames;
        public int CompletedFrames => Volatile.Read(ref _frames);

        public void CompleteFrame(int frames) => Volatile.Write(ref _frames, frames);
    }
}
