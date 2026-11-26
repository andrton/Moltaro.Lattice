using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

// Keeps the authored values and exact positions of only the roots the tape can change.
internal sealed class BudgetChurnModel
{
    private readonly Dictionary<long, (LatticeRecord Value, LatticeRevision Revision)> _users = [];
    private readonly HashSet<long> _targets;
    private readonly HashSet<long> _absent = [];
    private readonly LatticeRevision[] _collections = new LatticeRevision[3];
    private readonly bool[] _grown = new bool[3];

    public BudgetChurnModel(ChurnTrace trace)
    {
        _targets = trace.Frames.Select(frame => frame.Key)
            .Concat(trace.Frames.Where(frame => frame.Kind == ChurnTrace.ChangeEnum.Exchange).Select(frame => frame.OldKey))
            .ToHashSet();
        _absent.UnionWith(_targets);
    }

    public ulong Sequence { get; private set; }

    public void Seed(LatticeRecord record, LatticeWriteResult write)
    {
        Commit(write);
        var revision = Revision(write.Position);
        switch (record.EntityType.Name)
        {
            case "User":
                var key = record.Get<long>("Id");
                if (_targets.Contains(key))
                {
                    _users.Add(key, (record, revision));
                    _absent.Remove(key);
                }

                if (key == ChurnTrace.CollectionUser)
                {
                    _collections[0] = revision;
                }
                break;

            case "Role" when record.Get<long>("Id") == ChurnTrace.CollectionRole:
                _collections[1] = revision;
                break;

            case "Setting" when record.Get<string>("Key") == ChurnTrace.CollectionSetting:
                _collections[2] = revision;
                break;
        }
    }

    public string? UserDependency(ChurnTrace.Frame frame)
    {
        if (frame.Kind == ChurnTrace.ChangeEnum.Replace)
        {
            return _users.ContainsKey(frame.Key) ? null : "replacement target is absent";
        }

        return _users.ContainsKey(frame.OldKey) && _absent.Contains(frame.Key)
            ? null
            : "exchange predecessor did not leave the expected live/absent pair";
    }

    public bool CollectionApplies(ChurnTrace.Frame frame) =>
        _grown[frame.CollectionTarget] != (frame.Collection == ChurnTrace.CollectionChangeEnum.Grow);

    public void ApplyUser(ChurnTrace.Frame frame, LatticeRecord row, LatticeWriteResult write)
    {
        Commit(write);
        bool exchange = frame.Kind == ChurnTrace.ChangeEnum.Exchange;
        if (exchange)
        {
            _users.Remove(frame.OldKey);
            _absent.Add(frame.OldKey);
            _absent.Remove(frame.Key);
        }

        // The insert is the second member of an exchange, while replacement is a single write.
        _users[frame.Key] = (row, Revision(write.Position, exchange ? 2u : 0u));
    }

    public void ApplyCollection(ChurnTrace.Frame frame, LatticeWriteResult write)
    {
        Commit(write);
        _grown[frame.CollectionTarget] = frame.Collection == ChurnTrace.CollectionChangeEnum.Grow;
        _collections[frame.CollectionTarget] = Revision(write.Position);
    }

    public void CheckCollections(EngineDatabase node, ChurnAudit audit, int extra)
    {
        audit.CheckCollections(node, _grown[0] ? extra : 0, _grown[1] ? extra : 0, _grown[2] ? extra : 0);
        ChurnAudit.Require(node.Set("User").TryGet(ChurnTrace.CollectionUser, out _, out var user) &&
            node.Set("Role").TryGet(ChurnTrace.CollectionRole, out _, out var role) &&
            node.Set("Setting").TryGet(ChurnTrace.CollectionSetting, out _, out var setting) &&
            (user, role, setting) == (_collections[0], _collections[1], _collections[2]),
            "Collection revision disagreed with its last successful mutation.");
    }

    public int CheckUsers(EngineDatabase node)
    {
        var users = node.Set("User");
        foreach (long key in _absent)
        {
            ChurnAudit.Require(!users.TryGet(key, out _), "A key absent from the committed model was found.");
        }

        foreach (var (key, expected) in _users)
        {
            ChurnAudit.Require(ChurnAudit.CheckRead(users, key, expected.Value) == expected.Revision,
                "User revision disagreed with its last successful mutation.");
        }

        return _users.Count + _absent.Count;
    }

    private void Commit(LatticeWriteResult write)
    {
        Sequence++;
        ChurnAudit.Require(write.Changed && write.Position.Sequence == Sequence,
            "A scheduled mutation did not produce the expected single changed transaction.");
    }

    private static LatticeRevision Revision(LatticePosition position, uint member = 0) =>
        new(position.History ^ ((ulong)member << 32), position.Sequence);
}
