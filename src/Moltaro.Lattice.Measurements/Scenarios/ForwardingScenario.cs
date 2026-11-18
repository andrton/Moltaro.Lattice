using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario forwarding: what a write through a child costs, and what it costs others (brief of Epic 1,
// sprint S12, Measured, the planner's block of 17 November 2026, point 9, and his decision after the
// study, point 16). A master of the users of UserDatabase accepts children on the loopback interface, and
// one child in the same process loads it. In its order:
// - Latency. One caller replaces users through the child, one write after the other, 20,000 times after a
//   warm-up: p50 and p99 of a write. Beside it the parts that can be measured apart without timing inside
//   the engine: the same replace on the master; the round trip of 64 bytes between two sockets on
//   loopback; the time from the end of a write on the master to the end of the child's wait for its
//   position, which is the stream to the child, its application and the wake-up of the waiter. The rest
//   of a forwarded write is the child's own work and the hand-offs between tasks: the request, its queue
//   and sender, the master's receiver and the reply.
// - Load. 16 callers forward replaces through the child for a window; then the same beside 4 callers that
//   replace on the master and 4 readers of the master; and the 4 and 4 alone, as the control.
// - Reads of the child. 4 readers read the child by key, alone and beside 16 callers forwarding through it;
//   and 4 readers read it with a session the child has reached, which reads the clock first.
// Every part runs twice, in turn, so that a drift of the machine shows. The callers and readers are
// threads of their own; the callers block on each write.
internal static class ForwardingScenario
{
    public const string Name = "forwarding";

    private const int Timed = 20_000;

    private const int WarmUpWrites = 2_000;

    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(0.5);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<string> Run()
    {
        using var users = UserDatabase.Load(listenUri: new Uri("lattice://127.0.0.1:0"));
        using var child = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = users.ListenUri,
        });

        child.OpenAsync().AsTask().GetAwaiter().GetResult();

        var childUsers = child.Set("User");
        var keys = users.Keys(ReadByKeyScenario.KeySeed, ReadSeries.Reads);
        var lines = new List<string>
        {
            users.Fixture,
            $"{users.Loaded}; one child in the same process, loaded at {child.Position}",
            "",
        };

        for (var round = 1; round <= 2; round++)
        {
            lines.Add($"Round {round}");
            lines.Add($"Forwarded replace, 1 caller: {Latency(index => childUsers.ReplaceAsync(childUsers.Get(1 + (index % 1_000))!).AsTask().GetAwaiter().GetResult())}");
            lines.Add($"The same replace on the master: {Latency(index => users.Replace(index % 1_000))}");
            lines.Add($"Round trip of 64 bytes on loopback: {RoundTrip()}");
            lines.Add($"From the end of a write on the master to the end of the child's wait for its position: {ToTheChild(users, child)}");

            var callers = Enumerable.Range(0, 16).Select(caller => Forwarder(childUsers, caller)).ToArray();
            var ownWriters = Enumerable.Range(0, 4).Select(caller => (Action)(() => users.Replace(100_000 - 1 - caller))).ToArray();
            var masterReaders = Enumerable.Range(0, 4).Select(reader => Reader(users.ByName, keys, reader)).ToArray();
            var childReaders = Enumerable.Range(0, 4).Select(reader => Reader(childUsers, keys, reader)).ToArray();
            var sessionReaders = Enumerable.Range(0, 4).Select(reader => SessionReader(childUsers, child, keys, reader)).ToArray();

            lines.Add($"16 callers forwarding through the child: {Series(("forwarded", callers))}");
            lines.Add($"16 callers forwarding beside 4 callers and 4 readers of the master: {Series(("forwarded", callers), ("master writes", ownWriters), ("master reads", masterReaders))}");
            lines.Add($"4 callers and 4 readers of the master alone: {Series(("master writes", ownWriters), ("master reads", masterReaders))}");
            lines.Add($"4 readers of the child alone: {Series(("child reads", childReaders))}");
            lines.Add($"4 readers of the child beside 16 callers forwarding through it: {Series(("child reads", childReaders), ("forwarded", callers))}");
            lines.Add($"4 readers of the child with a session it has reached: {Series(("session reads", sessionReaders))}");
            lines.Add("");
        }

        return lines;
    }

    // A caller that replaces users of its own through the child, read once from the child.
    private static Action Forwarder(ILatticeSet childUsers, int caller)
    {
        var own = Enumerable.Range(0, 100).Select(index => childUsers.Get((long)(1_000 + (caller * 100) + index + 1))!).ToArray();
        var next = 0;

        return () => childUsers.ReplaceAsync(own[next++ % own.Length]).AsTask().GetAwaiter().GetResult();
    }

    private static Action Reader(ILatticeSet set, long[] keys, int reader)
    {
        var next = reader * (keys.Length / 4);

        return () =>
        {
            if (set.Get(keys[next]) is null)
            {
                throw new InvalidOperationException($"The user {keys[next]} was not found.");
            }

            next = next + 1 == keys.Length ? 0 : next + 1;
        };
    }

    // A reader with a session at the position the child has reached when it starts.
    private static Action SessionReader(ILatticeSet set, EngineDatabase child, long[] keys, int reader)
    {
        var session = new LatticeSession(child.Position);
        var next = reader * (keys.Length / 4);

        return () =>
        {
            if (!set.GetAsync(keys[next], session).AsTask().GetAwaiter().GetResult().Found)
            {
                throw new InvalidOperationException($"The user {keys[next]} was not found.");
            }

            next = next + 1 == keys.Length ? 0 : next + 1;
        };
    }

    // p50 and p99 of an operation made Timed times after a warm-up.
    private static string Latency(Action<int> operation)
    {
        for (var index = 0; index < WarmUpWrites; index++)
        {
            operation(index);
        }

        var times = new long[Timed];

        for (var index = 0; index < Timed; index++)
        {
            var before = Stopwatch.GetTimestamp();

            operation(index);
            times[index] = Stopwatch.GetTimestamp() - before;
        }

        return Percentiles(times);
    }

    // The round trip of 64 bytes between two connected sockets on loopback, each side its own thread.
    private static string RoundTrip()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        client.Connect(listener.LocalEndPoint!);

        using var server = listener.Accept();

        server.NoDelay = true;

        var echo = new Thread(() =>
        {
            var bytes = new byte[64];

            try
            {
                while (Fill(server, bytes))
                {
                    server.Send(bytes);
                }
            }
            catch (SocketException)
            {
                // The client ended.
            }
        })
        {
            IsBackground = true,
        };

        echo.Start();

        var message = new byte[64];

        return Latency(_ =>
        {
            client.Send(message);
            Fill(client, message);
        });
    }

    private static bool Fill(Socket socket, byte[] bytes)
    {
        var read = 0;

        while (read < bytes.Length)
        {
            var count = socket.Receive(bytes, read, bytes.Length - read, SocketFlags.None);

            if (count == 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    // From the end of a write on the master to the end of the child's wait for its position.
    private static string ToTheChild(UserDatabase users, EngineDatabase child)
    {
        for (var index = 0; index < WarmUpWrites; index++)
        {
            users.Replace(index % 1_000);
            child.WaitForPositionAsync(users.Position, TimeSpan.FromMinutes(1)).AsTask().GetAwaiter().GetResult();
        }

        var times = new long[Timed];

        for (var index = 0; index < Timed; index++)
        {
            users.Replace(index % 1_000);

            var position = users.Position;
            var before = Stopwatch.GetTimestamp();

            child.WaitForPositionAsync(position, TimeSpan.FromMinutes(1)).AsTask().GetAwaiter().GetResult();
            times[index] = Stopwatch.GetTimestamp() - before;
        }

        return Percentiles(times);
    }

    // The groups of threads given, each running its operation over and over, through a warm-up and a
    // window: per group the operations a second and p50 and p99 of one; then the processors busy.
    private static string Series(params (string Name, Action[] Threads)[] groups)
    {
        var phase = 0;
        var all = groups.SelectMany((group, index) => group.Threads.Select(operation => (Group: index, Operation: operation))).ToArray();
        var counts = new long[all.Length];
        var times = new List<long>[all.Length];
        using var start = new Barrier(all.Length + 1);
        var threads = new Thread[all.Length];

        for (var index = 0; index < all.Length; index++)
        {
            var at = index;

            times[at] = new List<long>(1 << 16);
            threads[at] = new Thread(() =>
            {
                start.SignalAndWait();

                while (true)
                {
                    var now = Volatile.Read(ref phase);

                    if (now == 2)
                    {
                        return;
                    }

                    var before = Stopwatch.GetTimestamp();

                    all[at].Operation();

                    if (now == 1)
                    {
                        times[at].Add(Stopwatch.GetTimestamp() - before);
                        counts[at]++;
                    }
                }
            })
            {
                IsBackground = true,
            };
            threads[at].Start();
        }

        start.SignalAndWait();
        Thread.Sleep(WarmUp);

        var processor = Process.GetCurrentProcess().TotalProcessorTime;
        var machine = MachineProcessors.BusyTicks();
        var started = Stopwatch.GetTimestamp();

        Volatile.Write(ref phase, 1);
        Thread.Sleep(Window);
        Volatile.Write(ref phase, 2);

        var elapsed = Stopwatch.GetElapsedTime(started);

        machine = MachineProcessors.BusyTicks() - machine;
        processor = Process.GetCurrentProcess().TotalProcessorTime - processor;

        foreach (var thread in threads)
        {
            thread.Join();
        }

        var parts = new List<string>();

        for (var group = 0; group < groups.Length; group++)
        {
            var members = Enumerable.Range(0, all.Length).Where(index => all[index].Group == group).ToArray();
            var count = members.Sum(index => counts[index]);
            var merged = members.SelectMany(index => times[index]).ToArray();

            parts.Add($"{groups[group].Name} {ReadSeries.Number((long)(count / elapsed.TotalSeconds))}/s, {Percentiles(merged)}");
        }

        parts.Add($"processors busy: the process {(processor.Ticks / (double)elapsed.Ticks).ToString("F1", CultureInfo.InvariantCulture)}, the machine {(machine is { } ticks ? (ticks / (double)elapsed.Ticks).ToString("F1", CultureInfo.InvariantCulture) : "not read")}");

        return string.Join("; ", parts);
    }

    private static string Percentiles(long[] ticks)
    {
        if (ticks.Length == 0)
        {
            return "none";
        }

        Array.Sort(ticks);

        return $"p50 {Microseconds(ticks[(int)Math.Min(ticks.Length - 1, Math.Ceiling(0.50 * ticks.Length) - 1)])}, p99 {Microseconds(ticks[(int)Math.Min(ticks.Length - 1, Math.Ceiling(0.99 * ticks.Length) - 1)])}";
    }

    private static string Microseconds(long ticks) =>
        $"{(ticks * 1_000_000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture)} µs";
}
