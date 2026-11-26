using System.Globalization;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Counts application values independently of the storage layout. Repeated text is counted per
// field. A Boolean counts as one byte, integer keys and references as eight. Descriptors, indexes,
// headers, dictionary encodings, reserve and allocator capacity belong to the separate native ledger.
internal sealed class ChurnVolume
{
    private static readonly string[] Sets = ["User", "Group", "Role", "Setting"];

    private ChurnVolume(Volume initial, Volume measuredStart, Volume final)
    {
        Initial = initial;
        MeasuredStart = measuredStart;
        Final = final;
    }

    public Volume Initial { get; }
    public Volume MeasuredStart { get; }
    public Volume Final { get; }

    public static ChurnVolume Model(IEnumerable<LatticeRecord> seed, ChurnTrace trace,
        Func<int, bool>? userChanged = null, Func<int, bool>? collectionChanged = null)
    {
        var changed = trace.Frames.SelectMany(frame => new[] { frame.Key, frame.OldKey }).ToHashSet();
        var users = new Dictionary<long, RowVolume>();
        var volume = new Accumulator();
        foreach (var row in seed)
        {
            var size = SizeOf(row);
            volume.AddRow(size);
            if (row.EntityType.Name == "User" && changed.Contains(row.Get<long>("Id")))
            {
                users.Add(row.Get<long>("Id"), size);
            }
        }

        var initial = volume.Read();
        Volume? measuredStart = null;
        var extra = CollectionSizes(trace.CollectionExtra);
        foreach (var frame in trace.Frames)
        {
            if (frame.Number == trace.WarmFrames)
            {
                measuredStart = volume.Read();
            }

            if (userChanged?.Invoke(frame.Number) != false)
            {
                long removed = frame.Kind == ChurnTrace.ChangeEnum.Exchange ? frame.OldKey : frame.Key;
                ChurnAudit.Require(users.Remove(removed, out var previous), "The volume model removed an absent user.");
                volume.RemoveRow(previous);
                var next = SizeOf(frame);
                users.Add(frame.Key, next);
                volume.AddRow(next);
            }

            if (collectionChanged?.Invoke(frame.Number) != false)
            {
                if (frame.Collection == ChurnTrace.CollectionChangeEnum.Grow)
                {
                    volume.ChangeCollection(extra[frame.CollectionTarget], 1);
                }
                else if (frame.Collection == ChurnTrace.CollectionChangeEnum.Trim)
                {
                    volume.ChangeCollection(extra[frame.CollectionTarget], -1);
                }
            }
        }

        var final = volume.Read();
        ChurnAudit.Require(measuredStart is not null && (userChanged is not null || Equivalent(measuredStart, final)),
            "The cyclic trace did not preserve the expected logical volume across the measured window.");
        return new ChurnVolume(initial, measuredStart!, final);
    }

    public static Volume Observe(EngineDatabase database, Volume expected)
    {
        var observed = Observe(database);
        ChurnAudit.Require(Equivalent(expected, observed), "Public enumeration disagreed with the authored logical volume.");
        return observed;
    }

    public static Volume Observe(EngineDatabase database)
    {
        var observed = new Accumulator();
        foreach (var name in Sets)
        {
            foreach (var row in database.Set(name).Enumerate())
            {
                observed.AddRow(SizeOf(row));
            }
        }

        return observed.Read();
    }

    public static bool Equivalent(Volume expected, Volume observed) =>
        expected.Rows == observed.Rows && expected.ScalarBytes == observed.ScalarBytes &&
        expected.Utf8Bytes == observed.Utf8Bytes &&
        expected.UserDisplayNameUtf8Sizes.SequenceEqual(observed.UserDisplayNameUtf8Sizes);

    private static RowVolume SizeOf(LatticeRecord row)
    {
        int Text(string field) => Encoding.UTF8.GetByteCount(row.Get<string>(field)!);
        return row.EntityType.Name switch
        {
            "User" => new RowVolume(
                sizeof(long) + sizeof(bool) + sizeof(long) * row.Get<List<long>>("Groups")!.Count,
                Text("Login") + Text("Email") + Text("DisplayName") + Text("Region"), Text("DisplayName")),
            "Group" => new RowVolume(sizeof(long) + sizeof(long) * row.Get<List<long>>("Roles")!.Count, Text("Name")),
            "Role" => new RowVolume(sizeof(long), Text("Name") + row.Get<HashSet<string>>("Permissions")!
                .Sum(Encoding.UTF8.GetByteCount)),
            "Setting" => new RowVolume(0, Text("Key") + Text("Section") + Text("Name") + Text("Value") +
                row.Get<Dictionary<string, string>>("Options")!.Sum(pair =>
                    Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value))),
            _ => throw new InvalidOperationException("The reference fixture has an unexpected entity type."),
        };
    }

    private static RowVolume SizeOf(ChurnTrace.Frame frame)
    {
        int digits = frame.Key.ToString("D8", CultureInfo.InvariantCulture).Length;
        int utf8 = "user-".Length + digits + "u".Length + digits + "@example.test".Length +
            frame.Size + Encoding.UTF8.GetByteCount(frame.Region);
        return new RowVolume(sizeof(long) + sizeof(bool) + 3 * sizeof(long), utf8, frame.Size);
    }

    private static RowVolume[] CollectionSizes(int extra)
    {
        int permissions = 0;
        int options = 0;
        for (int item = 0; item < extra; item++)
        {
            permissions += Encoding.UTF8.GetByteCount(ChurnTrace.Permission(item));
            options += Encoding.UTF8.GetByteCount(ChurnTrace.SettingKey(item)) +
                Encoding.UTF8.GetByteCount(ChurnTrace.Value(item, 0, 70));
        }

        return [new RowVolume(extra * sizeof(long), 0), new RowVolume(0, permissions), new RowVolume(0, options)];
    }

    internal sealed record Volume(int Rows, long ScalarBytes, long Utf8Bytes,
        IReadOnlyDictionary<int, int> UserDisplayNameUtf8Sizes);

    private readonly record struct RowVolume(int ScalarBytes, int Utf8Bytes, int UserDisplayNameBytes = -1);

    private sealed class Accumulator
    {
        private readonly SortedDictionary<int, int> _userNames = new();
        private int _rows;
        private long _scalar;
        private long _utf8;

        public void AddRow(RowVolume row) => ChangeRow(row, 1);
        public void RemoveRow(RowVolume row) => ChangeRow(row, -1);

        public void ChangeCollection(RowVolume row, int direction)
        {
            _scalar += direction * row.ScalarBytes;
            _utf8 += direction * row.Utf8Bytes;
        }

        public Volume Read() => new(_rows, _scalar, _utf8, new SortedDictionary<int, int>(_userNames));

        private void ChangeRow(RowVolume row, int direction)
        {
            _rows += direction;
            ChangeCollection(row, direction);
            int size = row.UserDisplayNameBytes;
            if (size >= 0)
            {
                int count = _userNames.GetValueOrDefault(size) + direction;
                if (count == 0)
                {
                    _userNames.Remove(size);
                }
                else
                {
                    _userNames[size] = count;
                }
            }
        }
    }
}
