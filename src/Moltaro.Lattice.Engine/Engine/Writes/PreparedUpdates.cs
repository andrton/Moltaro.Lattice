using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Replication.Updates;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Roots.Edits;

namespace Moltaro.Lattice.Engine.Writes;

// The delivery data of a successful preparation, owned by that attempt.
// Only update members with effective commands have a sparse effect.
internal sealed class PreparedUpdates(MemoryBudget? budget) : IDisposable
{
    private Dictionary<uint, SparseChange>? _effects;

    private UpdateBitmap _reply;
    private MemoryLease? _work;
    private bool _keepsCallerResults;

    public MemoryLease? Work => budget is null ? null : _work ??= new MemoryLease(budget);

    public byte[] ResultBits(UpdateInput input)
    {
        if (Work is not { } owner)
        {
            return UpdateOutcome.BitsFor(input.Commands.Length);
        }

        var bits = owner.Allocate<byte>(UpdateOutcome.BitmapLength(input.Commands.Length), nameof(PreparedUpdates));
        owner.Own(() =>
        {
            if (!_keepsCallerResults)
            {
                input.Outcome.ClearBits(bits);
            }
        });
        return bits;
    }

    public void PublishCallerResults() => _keepsCallerResults = true;

    public void Add(uint member, ulong predecessor, StructureLayout layout, ReadOnlySpan<ResolvedCommand> commands)
    {
        var length = UpdateCommands.Length(layout, commands);
        var effects = _effects ??= BudgetDictionaries.Create<uint, SparseChange>(budget, nameof(PreparedUpdates));
        effects.EnsureCapacity(effects.Count + 1, budget, nameof(PreparedUpdates));
        var saved = budget?.Allocate<ResolvedCommand>(commands.Length, nameof(PreparedUpdates)) ?? new ResolvedCommand[commands.Length];
        try
        {
            commands.CopyTo(saved);
            effects.Add(member, new SparseChange(predecessor, saved, length));
        }
        catch
        {
            budget?.Release(saved);
            throw;
        }
    }

    public SparseChange? EffectOf(uint member) =>
        _effects is { } effects && effects.TryGetValue(member, out var effect) ? effect : null;

    public void SetReply(UpdateReplies? replies) => _reply = replies?.Pack(budget) ?? default;

    // Recording the reply transfers its already charged buffer to the connection.
    public UpdateBitmap TakeReply()
    {
        var reply = _reply;
        _reply = default;
        return reply;
    }

    public void Dispose()
    {
        if (TakeReply().Bits is { } bits)
        {
            budget?.Release(bits);
        }

        var effects = _effects;
        _effects = null;
        if (effects is not null && budget is not null)
        {
            foreach (var effect in effects.Values)
            {
                budget.Release(effect.Commands);
            }

            effects.Clear();
            budget.ReleaseDictionary(effects, includesOwner: true);
        }

        var work = _work;
        _work = null;
        work?.Dispose();
    }
}
