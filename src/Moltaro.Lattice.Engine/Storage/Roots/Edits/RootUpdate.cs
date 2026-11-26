using System.Runtime.InteropServices;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// Plans an ordered update from the published version under the writing lock.
// It holds only changed targets and cached collection edits. New slots and
// references are taken by the writer after the commands have succeeded.
internal sealed class RootUpdate
{
    private readonly Arena _arena;
    private readonly RootLayout _layout;
    private readonly ArenaAddress _source;
    private readonly Dictionary<int, object?> _changes = [];

    private readonly List<ResolvedCommand> _effects = [];

    private RootUpdate(Arena arena, RootLayout layout, ArenaAddress source)
    {
        _arena = arena;
        _layout = layout;
        _source = source;
    }

    public RootUpdate(Arena arena, RootLayout layout, ArenaAddress source, UpdateCommand[] commands)
        : this(arena, layout, source)
    {
        Bits = UpdateOutcome.BitsFor(commands.Length);

        for (var index = 0; index < commands.Length; index++)
        {
            if (Apply(commands[index], out var effective))
            {
                UpdateOutcome.SetChanged(Bits, index);
                _effects.Add(effective);
                Changed = true;
            }
        }
    }

    // Replay uses the same edits but verifies each resolved action against the
    // exact predecessor. A failed plan owns no arena slots.
    public RootUpdate(Arena arena, RootLayout layout, ArenaAddress source, ReadOnlySpan<ResolvedCommand> commands)
        : this(arena, layout, source)
    {
        foreach (ref readonly var expected in commands)
        {
            var command = expected.Command;
            if (!Apply(command, out var actual) ||
                actual.ReplacesMapValue != expected.ReplacesMapValue ||
                (command.First is string spelling && actual.Command.First is string found && !StringComparer.Ordinal.Equals(spelling, found)))
            {
                throw TargetMissing(layout.EntityType, command.Name, "does not match the committed collection action.");
            }
        }

        Changed = true;
    }

    public bool Changed { get; }

    public byte[] Bits { get; } = [];

    public ReadOnlySpan<ResolvedCommand> Effects => CollectionsMarshal.AsSpan(_effects);

    private bool Apply(in UpdateCommand command, out ResolvedCommand resolved)
    {
        var payload = _arena.Payload(_source);
        var structure = _layout.Structure;
        RequireParents(payload, structure, command);
        if (command.Kind == UpdateCommandKindEnum.Assign)
        {
            _changes[command.Target] = command.First;
            resolved = new ResolvedCommand(command);
            return true;
        }

        var element = structure.ElementOf(command.Target);
        if (!_changes.TryGetValue(command.Target, out var staged))
        {
            ref readonly var place = ref structure.Place(command.Target);
            if (IsNull(payload, place))
            {
                throw MissingCollection(command);
            }

            var descriptor = ResidentDescriptor.Read(payload[(place.Offset - BlockHeader.Size)..]);
            var stored = new StoredCollection(_arena, element, _layout.StringEncoding, _source, descriptor);
            staged = new EditedCollection(element, stored, assigned: null);
        }
        else if (staged is null)
        {
            throw MissingCollection(command);
        }
        else if (staged is not EditedCollection)
        {
            staged = new EditedCollection(element, stored: null, staged);
        }

        var collection = (EditedCollection)staged;
        _changes[command.Target] = collection;
        return collection.Edit(command, _layout.EntityType, element, out resolved);
    }

    // An intermediate owned value is flattened into the root but still has
    // its own presence bit. A leaf's null bit cannot distinguish an absent
    // parent from an explicitly null field.
    private void RequireParents(ReadOnlySpan<byte> payload, StructureLayout structure, in UpdateCommand command)
    {
        var parent = command.Path[0].Ordinal;
        for (var depth = 1; depth < command.Path.Length; depth++)
        {
            ref readonly var place = ref structure.Place(parent);
            if (IsNull(payload, place))
            {
                throw TargetMissing(_layout.EntityType, command.Name, $"has no owned parent '{place.Field.Name}'.");
            }

            parent = structure.ChildOf(parent, command.Path[depth]);
        }
    }

    private static bool IsNull(ReadOnlySpan<byte> payload, in StoredField place) =>
        place.IsNullable && (payload[place.NullBit / 8] & (1 << (place.NullBit % 8))) != 0;

    // The fresh image takes the fixed bytes only. Every descriptor starts
    // unplaced until it is rebuilt or its ownership is recorded explicitly.
    // That lets the normal writer give back a partly filled image.
    public void Place(ref ValueHolder holder)
    {
        var structure = _layout.Structure;
        var fixedBytes = structure.DescriptorOffset - BlockHeader.Size;
        _arena.Payload(_source)[..fixedBytes].CopyTo(holder.At(BlockHeader.Size, fixedBytes));

        foreach (var (target, value) in _changes)
        {
            ref readonly var place = ref structure.Place(target);
            if (place.Offset < structure.DescriptorOffset)
            {
                PlaceAssignment(ref holder, structure, target, place, value);
            }
        }

        for (var descriptor = 0; descriptor < structure.DescriptorCount && !holder.Refused; descriptor++)
        {
            var target = structure.DescriptorPosition(descriptor);
            ref readonly var place = ref structure.Place(target);
            if (!_changes.TryGetValue(target, out var value) || value is EditedCollection { Changed: false })
            {
                holder.Retain(place, place.Offset, _source, place.Offset);
            }
            else if (value is EditedCollection collection)
            {
                var reuse = IsAssociation(place.Type) && collection.Stored.HasValue &&
                    _arena.CollectionTails?.HasSlots(holder.Root, place.Offset) == true;
                collection.Place(ref holder, structure, target, reuse);
            }
            else
            {
                PlaceAssignment(ref holder, structure, target, place, value);
            }
        }
    }

    // The placed root belongs to the publication or rollback that follows.
    // Only exact flat sparse targets can borrow or contribute a former lookup.
    public void PrepareCollectionTails(RootHandle handle, ArenaAddress root)
    {
        var structure = _layout.Structure;
        var tails = _arena.CollectionTails;
        var hasOwner = tails?.HasOwner(handle) == true;
        if (hasOwner)
        {
            // An update of an unrelated field preserves a lease. A full
            // replacement has no sparse preparation and ends the lease.
            tails!.PrepareSparse(root);
        }

        foreach (var (target, value) in _changes)
        {
            ref readonly var place = ref structure.Place(target);
            if (!IsAssociation(place.Type))
            {
                continue;
            }

            if (value is not EditedCollection { Stored: { } previous } edit)
            {
                if (hasOwner)
                {
                    tails!.PrepareEnd(root, handle, place.Offset);
                }

                continue;
            }

            if (!edit.Changed)
            {
                continue;
            }

            var count = edit.Count;
            var old = previous.Descriptor;
            var canContribute = count < previous.Count && old.Kind == DescriptorKindEnum.Lookup && CollectionLookup.IsChunked(old);
            if (!hasOwner && !canContribute)
            {
                continue;
            }

            var need = CollectionLookup.SlotsNeeded(count);
            var placed = ResidentDescriptor.Read(_arena.Payload(root)[(place.Offset - BlockHeader.Size)..]);
            var active = placed.Kind == DescriptorKindEnum.Lookup ? new CollectionLookup(_arena, placed).Capacity : 0;
            // Do not reserve a former table for ordinary oscillation. At exact
            // equality a real spare remains useful without starting the timer.
            var former = canContribute && active + new CollectionLookup(_arena, old).Capacity >= 4 * need
                ? ValueBlocks.AddressOf(old) : default;
            if (!hasOwner && former.IsNull)
            {
                continue;
            }

            tails ??= _arena.TrackCollectionTails();
            tails.PrepareChange(root, handle, place.Offset, former, active, need);
        }
    }

    private static bool IsAssociation(StoredTypeEnum type) => type is StoredTypeEnum.Map or StoredTypeEnum.Set;

    private static void PlaceAssignment(ref ValueHolder holder, StructureLayout structure, int target, in StoredField place, object? value)
    {
        if (value is not null)
        {
            NestedValues.Write(ref holder, structure, 0, target, value);
            return;
        }

        holder.At(structure.Start + place.NullBit / 8, 1)[0] |= (byte)(1 << (place.NullBit % 8));
        holder.At(place.Offset, place.Width).Clear();
    }

    private LatticeException MissingCollection(in UpdateCommand command) =>
        TargetMissing(_layout.EntityType, command.Name, "has no collection to edit.");

    public static LatticeException TargetMissing(LatticeEntityType type, string field, string reason) =>
        new(LatticeErrorEnum.UpdateTargetMissing, $"{type.Name}.{field} {reason}");
}
