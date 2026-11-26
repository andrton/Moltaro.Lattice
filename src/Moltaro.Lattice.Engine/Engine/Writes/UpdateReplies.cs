using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Replication.Forwarding;

namespace Moltaro.Lattice.Engine.Writes;

// Command outcomes retained in request order without retaining the commands.
// The same boundaries pack the master's reply and distribute it on the child.
internal sealed class UpdateReplies
{
    private readonly Part[] _updates;

    private UpdateReplies(Part[] updates, int count, bool onlyUpdates, byte[]? receiveBuffer)
    {
        _updates = updates;
        Count = count;
        OnlyUpdates = onlyUpdates;
        ReceiveBuffer = receiveBuffer;
    }

    public int Count { get; }

    public bool OnlyUpdates { get; }
    public byte[]? ReceiveBuffer { get; }

    public static UpdateReplies? Of(UpdateInput? update, MemoryLease? lease, bool receives = false)
    {
        if (update is null)
        {
            return null;
        }

        var parts = lease?.Allocate<Part>(1, nameof(UpdateReplies)) ?? new Part[1];
        var count = update.Commands.Length;
        parts[0] = new Part(update.Outcome, count, null);
        return Create(parts, count, onlyUpdates: true, receives, lease);
    }

    public static UpdateReplies? Of(BatchMember[] members, MemoryLease? lease, bool receives = false)
    {
        var updates = 0;
        var commands = 0;
        foreach (ref readonly var member in members.AsSpan())
        {
            if (member.Update is { } update)
            {
                updates++;
                commands = checked(commands + update.Commands.Length);
            }
        }

        if (updates == 0)
        {
            return null;
        }

        var parts = lease?.Allocate<Part>(updates, nameof(UpdateReplies)) ?? new Part[updates];
        var index = 0;
        foreach (ref readonly var member in members.AsSpan())
        {
            if (member.Update is { } update)
            {
                var count = update.Commands.Length;
                var output = receives && updates > 1 ? AllocateBits(count, lease) : null;
                parts[index++] = new Part(update.Outcome, count, output);
            }
        }

        return Create(parts, commands, updates == members.Length, receives, lease);
    }

    private static UpdateReplies Create(Part[] parts, int count, bool onlyUpdates, bool receives, MemoryLease? lease)
    {
        var input = receives ? AllocateBits(count, lease) : null;
        return lease?.Allocate(MemoryBudget.ObjectBytes(2L * IntPtr.Size + sizeof(int) + 1), nameof(UpdateReplies),
            () => new UpdateReplies(parts, count, onlyUpdates, input)) ?? new UpdateReplies(parts, count, onlyUpdates, input);
    }

    private static byte[] AllocateBits(int commands, MemoryLease? lease) =>
        lease?.Allocate<byte>(UpdateOutcome.BitmapLength(commands), nameof(UpdateReplies)) ?? UpdateOutcome.BitsFor(commands);

    public UpdateBitmap Pack(MemoryBudget? budget)
    {
        var bits = budget?.Allocate<byte>(UpdateOutcome.BitmapLength(Count), nameof(UpdateReplies)) ?? UpdateOutcome.BitsFor(Count);
        try
        {
            var offset = 0;
            foreach (var (outcome, count, _) in _updates)
            {
                Copy(outcome.PreparedBits, 0, bits, offset, count);
                offset += count;
            }

            return new UpdateBitmap(Count, bits);
        }
        catch
        {
            budget?.Release(bits);
            throw;
        }
    }

    public void Apply(byte[] bits)
    {
        if (_updates.Length == 1)
        {
            _updates[0].Outcome.SetBits(bits);
            return;
        }

        var offset = 0;
        foreach (var (outcome, count, output) in _updates)
        {
            Copy(bits, offset, output, 0, count);
            outcome.SetBits(output!);
            offset += count;
        }
    }

    private readonly record struct Part(UpdateOutcome Outcome, int Count, byte[]? Output);

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
