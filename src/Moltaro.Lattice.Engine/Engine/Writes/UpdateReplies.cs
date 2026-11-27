using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Replication.Forwarding;

namespace Moltaro.Lattice.Engine.Writes;

// Command outcomes retained in request order without retaining the commands.
// The same boundaries pack the master's reply and distribute it on the child.
internal sealed class UpdateReplies
{
    private readonly (UpdateOutcome Outcome, int Count)[] _updates;

    private UpdateReplies((UpdateOutcome Outcome, int Count)[] updates, int count, bool onlyUpdates)
    {
        _updates = updates;
        Count = count;
        OnlyUpdates = onlyUpdates;
    }

    public int Count { get; }

    public bool OnlyUpdates { get; }

    public static UpdateReplies? Of(UpdateInput? update) =>
        update is null ? null : new UpdateReplies([(update.Outcome, update.Commands.Length)], update.Commands.Length, onlyUpdates: true);

    public static UpdateReplies? Of(BatchMember[] members)
    {
        List<(UpdateOutcome, int)>? updates = null;
        var commands = 0;
        foreach (ref readonly var member in members.AsSpan())
        {
            if (member.Update is { } update)
            {
                var count = update.Commands.Length;
                (updates ??= []).Add((update.Outcome, count));
                commands = checked(commands + count);
            }
        }

        return updates is null ? null : new UpdateReplies([.. updates], commands, updates.Count == members.Length);
    }

    public UpdateBitmap Pack()
    {
        var bits = UpdateOutcome.BitsFor(Count);
        var offset = 0;
        foreach (var (outcome, count) in _updates)
        {
            Copy(outcome.PreparedBits, 0, bits, offset, count);
            offset += count;
        }

        return new UpdateBitmap(Count, bits);
    }

    public void Apply(ReadOnlySpan<byte> bits)
    {
        var offset = 0;
        foreach (var (outcome, count) in _updates)
        {
            var local = UpdateOutcome.BitsFor(count);
            Copy(bits, offset, local, 0, count);
            outcome.SetBits(local);
            offset += count;
        }
    }

    private static void Copy(ReadOnlySpan<byte> source, int from, Span<byte> target, int to, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (UpdateOutcome.HasBit(source, from + index))
            {
                target[(to + index) / 8] |= (byte)(1 << ((to + index) % 8));
            }
        }
    }
}
