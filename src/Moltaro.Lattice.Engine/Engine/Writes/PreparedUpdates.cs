using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Replication.Updates;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Roots.Edits;

namespace Moltaro.Lattice.Engine.Writes;

// The delivery data of a successful preparation, owned by that attempt.
// Only update members with effective commands have a sparse effect.
internal sealed class PreparedUpdates
{
    private Dictionary<uint, SparseChange>? _effects;

    public UpdateBitmap Reply { get; private set; }

    public void Add(uint member, ulong predecessor, StructureLayout layout, ReadOnlySpan<ResolvedCommand> commands)
    {
        var length = UpdateCommands.Length(layout, commands);
        (_effects ??= []).Add(member, new SparseChange(predecessor, commands.ToArray(), length));
    }

    public SparseChange? EffectOf(uint member) =>
        _effects is { } effects && effects.TryGetValue(member, out var effect) ? effect : null;

    public void SetReply(UpdateReplies? replies) => Reply = replies?.Pack() ?? default;
}
