using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal sealed class ChurnTrace
{
    public const int FramesPerSecond = 100;
    public const int UsersInSmoke = 1536;
    public const int Seed = 6035;
    public const long CollectionUser = 1;
    public const long CollectionRole = 1;
    public const string CollectionSetting = "setting-00001";

    private const int SwapSlots = 64;
    private const int WaveFrames = 10 * FramesPerSecond;
    private const int CyclicTail = 672;
    private const int MixedKeys = 256;
    private static readonly int[] Sizes = [32, 320, 2300, 12300];
    private readonly int _users;

    public ChurnTrace(bool smoke, int users, bool quick = false, bool cyclic = false, bool cyclicSmoke = false)
    {
        _users = users;
        Cyclic = cyclic;
        WarmFrames = smoke ? 20 : (quick ? 5 : 30) * FramesPerSecond;
        MeasuredFrames = cyclicSmoke ? 2 * WaveFrames : smoke ? 200 : (quick ? 48 : 300) * FramesPerSecond;
        FrameRate = cyclicSmoke ? 10 * FramesPerSecond : FramesPerSecond;
        Width = smoke ? 48 : quick ? 2048 : 384;
        CollectionExtra = smoke ? 32 : 256;
        var cycle = smoke ? 100 : quick ? WarmFrames + MeasuredFrames : WaveFrames;
        ReadBurst = quick ? 64 : 256;
        Frames = Prepare(WarmFrames + MeasuredFrames, cycle, quick);
        Digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Frames)));
    }

    public bool Cyclic { get; }
    public int FrameRate { get; }
    public int WarmFrames { get; }
    public int MeasuredFrames { get; }
    public int Width { get; }
    public int ReadBurst { get; }
    public int CollectionExtra { get; }
    public Frame[] Frames { get; }
    public string Digest { get; }

    public int UserWrites => Frames.Sum(frame => frame.Kind == ChangeEnum.Exchange ? 2 : 1);
    public int CollectionWrites => Frames.Count(frame => frame.Collection is CollectionChangeEnum.Grow or CollectionChangeEnum.Trim);
    public int Transactions => Frames.Length + CollectionWrites;
    public long TimedReadKey(int slot) => _users - slot;
    public int TimedReadKeys => Math.Min(384, _users / 4);

    public static string Value(long key, int phase, int size)
    {
        var prefix = $"k{key.ToString(CultureInfo.InvariantCulture)}-f{phase.ToString(CultureInfo.InvariantCulture)}:";
        return prefix.PadRight(size, (char)('A' + (phase % 26)));
    }

    public static string SettingKey(int item) => $"s23-option-{item:D4}";
    public static string Permission(int item) => $"s23-permission-{item:D4}";
    public static long Group(int item) => 1 + (item % FixtureRecords.Groups);

    public static LatticeRecord User(LatticeEntityType type, long key, int version, int size, string region)
    {
        var digits = key.ToString("D8", CultureInfo.InvariantCulture);
        return new LatticeRecord(type)
        {
            ["Id"] = key,
            ["Login"] = "user-" + digits,
            ["Email"] = "u" + digits + "@example.test",
            ["DisplayName"] = Value(key, version, size),
            ["Region"] = region,
            ["IsActive"] = version % 2 == 0,
            ["Groups"] = new List<long> { Group(0), Group(1), Group(2) },
        };
    }

    private Frame[] Prepare(int count, int cycle, bool quick)
    {
        var random = new FixtureRandom(Seed);
        var swaps = new bool[SwapSlots];
        var cyclicMixed = Cyclic ? CyclicMixedTail() : [];
        var frames = new Frame[count];
        for (var frame = 0; frame < frames.Length; frame++)
        {
            var place = frame % cycle;
            var wave = frame / cycle;
            var block = 33 + ((long)(wave % 2) * Width);
            var trimAt = cycle == 100 ? 10 : 50;
            var verifyAt = cycle == 100 ? 50 : quick ? 4500 : 650;
            var (collection, collectionTarget) = place switch
            {
                >= 0 and <= 2 => (CollectionChangeEnum.Grow, place),
                _ when place >= trimAt && place <= trimAt + 2 => (CollectionChangeEnum.Trim, place - trimAt),
                _ when place == verifyAt => (CollectionChangeEnum.CheckAfterHold, -1),
                _ => (CollectionChangeEnum.None, -1),
            };
            if (place < Width)
            {
                var key = block + place;
                // Reuse a small set for part of the wave and many temporary entries for the rest.
                var region = place % 8 == 0
                    ? $"churn-region-{wave:D3}-{place:D4}"
                    : $"region-{(1 + place % 16):D2}";
                frames[frame] = new Frame(frame, ChangeEnum.Replace, key, 0, quick ? 4064 : Sizes[^1], region, collection, collectionTarget);
                continue;
            }

            var trim = place - Width;
            int stride = quick ? 16 : 4;
            if (trim < Width * (stride - 1) / stride)
            {
                // Leave one logical value per group. No address affects the trace.
                var key = block + trim + trim / (stride - 1) + 1;
                frames[frame] = new Frame(frame, ChangeEnum.Replace, key, 0, Sizes[0], "region-01", collection, collectionTarget);
                continue;
            }

            int tail = place - CyclicTail;
            if (Cyclic ? IsCyclicSwap(tail) : place % 8 == 0)
            {
                var slot = Cyclic ? tail / 5 : random.Below(SwapSlots);
                long a = _users / 2 + slot;
                var b = (long)_users + slot + 1;
                var old = swaps[slot] ? b : a;
                var next = swaps[slot] ? a : b;
                swaps[slot] = !swaps[slot];
                frames[frame] = new Frame(frame, ChangeEnum.Exchange, next, old, Sizes[1], "region-02", collection, collectionTarget);
                continue;
            }

            if (Cyclic)
            {
                var move = cyclicMixed[tail];
                frames[frame] = new Frame(frame, ChangeEnum.Replace, move.Key, 0, move.Size, move.Region, collection, collectionTarget);
                continue;
            }

            var mixed = _users / 2 + SwapSlots + random.Below(_users / 8);
            frames[frame] = new Frame(frame, ChangeEnum.Replace, mixed, 0, Sizes[random.Below(quick ? Sizes.Length - 1 : Sizes.Length)],
                $"region-{(1 + random.Below(16)):D2}", collection, collectionTarget);
        }

        return frames;
    }

    // Exchanges cover each slot once per wave. Reusing the mixed schedule and alternating the
    // two sparse blocks gives the tape the same live volume at boundaries of equal parity.
    private (long Key, int Size, string Region)[] CyclicMixedTail()
    {
        var random = new FixtureRandom(Seed);
        var mixed = new (long Key, int Size, string Region)[WaveFrames - CyclicTail];
        for (var tail = 0; tail < mixed.Length; tail++)
        {
            if (!IsCyclicSwap(tail))
            {
                var key = _users / 2 + SwapSlots + random.Below(MixedKeys);
                mixed[tail] = (key, Sizes[random.Below(Sizes.Length)], $"region-{(1 + random.Below(16)):D2}");
            }
        }

        return mixed;
    }

    private static bool IsCyclicSwap(int tail) => tail % 5 == 0 && tail / 5 < SwapSlots;

    internal sealed record Frame(int Number, ChangeEnum Kind, long Key, long OldKey, int Size, string Region, CollectionChangeEnum Collection, int CollectionTarget);
    internal enum ChangeEnum { Replace, Exchange }
    internal enum CollectionChangeEnum { None, Grow, Trim, CheckAfterHold }
}
