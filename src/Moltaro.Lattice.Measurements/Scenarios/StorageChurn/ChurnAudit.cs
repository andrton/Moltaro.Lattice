using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal sealed class ChurnAudit
{
    private readonly long[] _groups;
    private readonly string[] _permissions;
    private readonly KeyValuePair<string, string>[] _options;
    private readonly LatticeRecord _heldUser;

    public ChurnAudit(IEnumerable<LatticeRecord> seed)
    {
        _groups = seed.First(row => row.EntityType.Name == "User" && row.Get<long>("Id") == ChurnTrace.CollectionUser)
            .Get<List<long>>("Groups")!.ToArray();
        _permissions = seed.First(row => row.EntityType.Name == "Role" && row.Get<long>("Id") == ChurnTrace.CollectionRole)
            .Get<HashSet<string>>("Permissions")!.ToArray();
        _options = seed.First(row => row.EntityType.Name == "Setting" && row.Get<string>("Key") == ChurnTrace.CollectionSetting)
            .Get<Dictionary<string, string>>("Options")!.ToArray();
        _heldUser = seed.First(row => row.EntityType.Name == "User" && row.Get<long>("Id") == 2);
    }

    public async ValueTask EditCollectionsAsync(EngineDatabase master, ChurnTrace.Frame frame, int extra, CancellationToken token)
    {
        bool grow = frame.Collection == ChurnTrace.CollectionChangeEnum.Grow;
        var update = frame.CollectionTarget switch
        {
            0 => master.Set("User").CreateUpdate(ChurnTrace.CollectionUser),
            1 => master.Set("Role").CreateUpdate(ChurnTrace.CollectionRole),
            2 => master.Set("Setting").CreateUpdate(ChurnTrace.CollectionSetting),
            _ => throw new InvalidOperationException("Unexpected trace collection."),
        };
        var outcomes = new LatticeCommandResult[extra];
        for (var item = 0; item < extra; item++)
        {
            outcomes[item] = frame.CollectionTarget switch
            {
                0 => grow ? update.ListAppend("Groups", ChurnTrace.Group(item)) : update.ListRemove("Groups", _groups.Length + extra - item - 1),
                1 => grow ? update.SetAdd("Permissions", ChurnTrace.Permission(item)) : update.SetRemove("Permissions", ChurnTrace.Permission(item)),
                2 => grow ? update.MapSet("Options", ChurnTrace.SettingKey(item), ChurnTrace.Value(item, 0, 70)) : update.MapRemove("Options", ChurnTrace.SettingKey(item)),
                _ => throw new InvalidOperationException("Unexpected trace collection."),
            };
        }

        Require((await update.CommitAsync(token)).Changed && outcomes.All(outcome => outcome.Changed),
            "A planned collection edit was not applied.");
    }

    public void CheckCollections(EngineDatabase node, int extra)
    {
        var users = node.Set("User");
        var roles = node.Set("Role");
        var settings = node.Set("Setting");

        long[] expectedGroups = [.. _groups, .. Enumerable.Range(0, extra).Select(ChurnTrace.Group)];
        var expectedPermissions = new HashSet<string>(_permissions.Concat(Enumerable.Range(0, extra).Select(ChurnTrace.Permission)));
        var expectedOptions = _options.Concat(Enumerable.Range(0, extra).Select(item =>
            KeyValuePair.Create(ChurnTrace.SettingKey(item), ChurnTrace.Value(item, 0, 70)))).ToDictionary();

        Require(users.TryGet(ChurnTrace.CollectionUser, out var user, out var userRev) && GroupsMatch(user!, expectedGroups),
            "Owned List disagreed with the trace.");
        Require(users.TryView(ChurnTrace.CollectionUser, view =>
        {
            var list = view["Groups"].List;
            if (list.Count != expectedGroups.Length || view.Revision != userRev)
            {
                return false;
            }
            int index = 0;
            foreach (var value in list)
            {
                if (value.Get<long>() != expectedGroups[index++])
                {
                    return false;
                }
            }

            return index == expectedGroups.Length;
        }, out var byListView) && byListView, "List view disagreed with the trace or revision.");

        Require(roles.TryGet(ChurnTrace.CollectionRole, out var role, out var roleRev) &&
            role!.Get<HashSet<string>>("Permissions")!.SetEquals(expectedPermissions), "Owned Set disagreed with the trace.");
        Require(roles.TryView(ChurnTrace.CollectionRole, view =>
        {
            var set = view["Permissions"].Set;
            if (set.Count != expectedPermissions.Count || view.Revision != roleRev)
            {
                return false;
            }
            var seen = new HashSet<string>();
            foreach (var value in set)
            {
                seen.Add(value.GetString());
            }

            return seen.SetEquals(expectedPermissions);
        }, out var bySetView) && bySetView, "Set view disagreed with the trace or revision.");

        Require(settings.TryGet(ChurnTrace.CollectionSetting, out var setting, out var settingRev) &&
            EqualMap(setting!.Get<Dictionary<string, string>>("Options")!, expectedOptions), "Owned Map disagreed with the trace.");
        Require(settings.TryView(ChurnTrace.CollectionSetting, view =>
        {
            var map = view["Options"].Map;
            if (map.Count != expectedOptions.Count || view.Revision != settingRev)
            {
                return false;
            }
            var seen = new Dictionary<string, string>();
            foreach (var entry in map)
            {
                seen.Add(entry.Key.GetString(), entry.Value.GetString());
            }

            return EqualMap(seen, expectedOptions);
        }, out var byMapView) && byMapView, "Map view disagreed with the trace or revision.");
    }

    // A separate correctness phase uses ordinary copy and held view paths and real publication on the child.
    // The authored value comes from the fixed fixture, and each new published revision is found at a position fence.
    public async Task CheckHeldRevisionsAsync(EngineDatabase master, EngineDatabase child, CancellationToken token)
    {
        const long key = 2;
        var originalName = _heldUser.Get<string>("DisplayName")!;
        var originalBytes = Encoding.UTF8.GetBytes(originalName);
        var masterSet = master.Set("User");
        var childSet = child.Set("User");
        var oldRevision = CheckRead(masterSet, key, _heldUser);
        Require(CheckRead(childSet, key, _heldUser) == oldRevision, "Initial master and child revisions differ.");
        using var entered = new CountdownEvent(2);
        using var publication = new ManualResetEventSlim();
        var participants = new[] { masterSet, childSet }.Select(set => Task.Factory.StartNew(() =>
        {
            Require(set.TryView(key, view =>
            {
                var bytes = view["DisplayName"].GetPart(0);
                Require(view.Revision == oldRevision && bytes.SequenceEqual(originalBytes), "Held view started on the wrong revision.");
                entered.Signal();
                Require(publication.Wait(TimeSpan.FromSeconds(10)), "Held view was not released.");
                Require(view.Revision == oldRevision && bytes.SequenceEqual(originalBytes) &&
                        view["DisplayName"].GetString() == originalName, "Held view lost its earlier revision.");
                return true;
            }, out var checkedView) && checkedView, "The held view missed.");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(10)), "The held views did not both start.");
            var replacement = ChurnTrace.User(masterSet.EntityType, key, 99, 2300, "region-02");
            var write = await masterSet.ReplaceAsync(replacement, token);
            Require(write.Changed, "The oracle replacement did not change a row.");
            await child.WaitForPositionAsync(write.Position, TimeSpan.FromSeconds(20), token);
            var nextRevision = CheckRead(masterSet, key, replacement);
            Require(nextRevision != oldRevision && CheckRead(childSet, key, replacement) == nextRevision,
                "A new read did not resolve the shared replacement revision.");
            CheckIndex(masterSet, replacement);
            CheckIndex(childSet, replacement);
        }
        finally
        {
            publication.Set();
            await Task.WhenAll(participants).WaitAsync(TimeSpan.FromSeconds(15), token);
        }

        var restored = await masterSet.ReplaceAsync(_heldUser, token);
        await child.WaitForPositionAsync(restored.Position, TimeSpan.FromSeconds(20), token);
        Require(CheckRead(masterSet, key, _heldUser) == CheckRead(childSet, key, _heldUser), "The restored revisions differ.");
    }

    public int CheckFinalUsers(EngineDatabase master, EngineDatabase child, ChurnTrace trace)
    {
        var expected = new Dictionary<long, ChurnTrace.Frame?>();
        foreach (var frame in trace.Frames)
        {
            expected[frame.Key] = frame;
            if (frame.Kind == ChurnTrace.ChangeEnum.Exchange)
            {
                expected[frame.OldKey] = null;
            }
        }

        var masterSet = master.Set("User");
        var childSet = child.Set("User");
        foreach (var (key, frame) in expected)
        {
            if (frame is null)
            {
                Require(!masterSet.TryGet(key, out _) && !childSet.TryGet(key, out _), "A removed identity is still live.");
                continue;
            }

            var expectedRow = ChurnTrace.User(masterSet.EntityType, key, frame.Number + 100, frame.Size, frame.Region);
            Require(CheckRead(masterSet, key, expectedRow) == CheckRead(childSet, key, expectedRow),
                "A changed final state has different revisions.");
        }

        var indexedFrame = trace.Frames[^1];
        var indexed = ChurnTrace.User(masterSet.EntityType, indexedFrame.Key, indexedFrame.Number + 100, indexedFrame.Size, indexedFrame.Region);
        CheckIndex(masterSet, indexed);
        CheckIndex(childSet, indexed);
        return expected.Count;
    }

    private static LatticeRevision CheckRead(ILatticeSet set, long key, LatticeRecord authored)
    {
        Require(set.TryGet(key, out var record, out var revision) && revision != default &&
            record!.Get<long>("Id") == key && record.Get<string>("DisplayName") == authored.Get<string>("DisplayName") &&
            record.Get<string>("Region") == authored.Get<string>("Region") &&
            record.Get<string>("Login") == authored.Get<string>("Login") && record.Get<string>("Email") == authored.Get<string>("Email") &&
            record.Get<bool>("IsActive") == authored.Get<bool>("IsActive") && GroupsMatch(record, authored.Get<List<long>>("Groups")!),
            "An owned User read disagreed with its authored state.");
        Require(set.TryView(key, view =>
        {
            var groups = view["Groups"].List;
            var expected = authored.Get<List<long>>("Groups")!;
            if (view.Revision != revision || view["Id"].Get<long>() != key || groups.Count != expected.Count ||
                view["DisplayName"].GetString() != authored.Get<string>("DisplayName") ||
                view["Region"].GetString() != authored.Get<string>("Region") ||
                view["Login"].GetString() != authored.Get<string>("Login") ||
                view["Email"].GetString() != authored.Get<string>("Email") ||
                view["IsActive"].Get<bool>() != authored.Get<bool>("IsActive"))
            {
                return false;
            }

            int position = 0;
            foreach (var value in groups)
            {
                if (value.Get<long>() != expected[position++])
                {
                    return false;
                }
            }

            return position == expected.Count;
        }, out var checkedView) && checkedView, "A User view disagreed with its authored state and revision.");
        return revision;
    }

    private static void CheckIndex(ILatticeSet users, LatticeRecord expected)
    {
        string region = expected.Get<string>("Region")!;
        string login = expected.Get<string>("Login")!;
        var range = IndexKey.Equal(region).Range(IndexBound.Inclusive(login), IndexBound.Inclusive(login));
        var row = users.Index("ByRegionAndLogin").Find(range).Single();
        Require(row.Get<long>("Id") == expected.Get<long>("Id") && row.Get<string>("DisplayName") == expected.Get<string>("DisplayName"),
            "Named index returned a different entity from its primary lookup.");
    }

    private static bool EqualMap(Dictionary<string, string> observed, Dictionary<string, string> expected) =>
        observed.Count == expected.Count && expected.All(pair => observed.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool GroupsMatch(LatticeRecord row, IReadOnlyList<long> expected) =>
        row.Get<List<long>>("Groups")!.SequenceEqual(expected);

    public static void Require(bool condition, string failure)
    {
        if (!condition)
        {
            throw new InvalidOperationException(failure);
        }
    }
}
