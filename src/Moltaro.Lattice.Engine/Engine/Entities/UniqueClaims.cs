using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Entities;

// What a prepared and unpublished write batch claims and frees in the unique indexes of its entity types,
// sorted, so that a later write asks the batch who claims a value or who frees one by a binary search and
// not by a scan of its changes (Writes.WriteCoordinator, FindClaimant and FindWriteThatFrees;
// docs/design/write-path.md, "A write batch"). For each unique index in which a
// change sets a value with no null part, the places of those changes sorted by the order of the index
// applied to their new roots; and the root entries of the changes that replace or delete a stored entity,
// sorted, each with its place. Made by the publisher on the first question (ChangePublisher.ClaimsOf),
// under the lock of the writes, from the changes of the batch, which do not change until the batch is
// published or given back; its record lets go of it then (Writes.PreparedWrite). Only the holder of the
// lock of the writes uses it.
internal sealed class UniqueClaims
{
    private readonly ChangePublisher _publisher;

    private readonly Arena _arena;

    private readonly PreparedChange[] _changes;

    // One entry for each unique index of an entity type in which a change sets a value: the table, the place
    // of the index among its indexes, and the places of the changes, sorted.
    private readonly List<(EntityTable Table, int Index, List<int> Places)> _claims = [];

    // The root entries of the changes of a stored entity, sorted, with the place of each change.
    private readonly (ulong Handle, int Place)[] _owners;

    public UniqueClaims(ChangePublisher publisher, Arena arena, PreparedChange[] changes, int count)
    {
        _publisher = publisher;
        _arena = arena;
        _changes = changes;

        var owners = new List<(ulong Handle, int Place)>();

        for (var place = 0; place < count; place++)
        {
            ref readonly var change = ref changes[place];

            if (change.Kind != ChangeKindEnum.Insert)
            {
                owners.Add((change.Handle.Packed, place));
            }

            if (change.Kind != ChangeKindEnum.Delete && change.Table.HasUniqueIndex)
            {
                AddClaims(change, place);
            }
        }

        _owners = [.. owners];
        _owners.AsSpan().Sort();

        foreach (var (table, index, places) in _claims)
        {
            CollectionsMarshal.AsSpan(places).Sort(new ClaimOrder(table.Indexes[index].Order, arena, table.Layout!, changes));
        }
    }

    // Whether a change of the batch sets or moves a value of a unique index to the value the change given,
    // of another entity, sets or moves there, as ChangePublisher.ClaimsSameKey decides it for two changes.
    public bool Claims(in PreparedChange change)
    {
        var layout = change.Table.Layout!;
        var mine = new RootReader(_arena, layout, change.Root);

        foreach (var (table, index, places) in _claims)
        {
            if (!ReferenceEquals(table, change.Table) || !_publisher.SetsValue(change, index) || table.Indexes[index].Layout.HasNull(mine))
            {
                continue;
            }

            var order = table.Indexes[index].Order;
            var (low, high) = (0, places.Count - 1);

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var compared = order.CompareKeys(mine, new RootReader(_arena, layout, _changes[places[middle]].Root));

                if (compared == 0)
                {
                    return true;
                }

                (low, high) = compared < 0 ? (low, middle - 1) : (middle + 1, high);
            }
        }

        return false;
    }

    // Whether the change of the batch of the root entry given gives up the value its published root holds in
    // the unique index at the place given, as ChangePublisher.Frees decides it.
    public bool Frees(RootHandle owner, int index)
    {
        var (low, high) = (0, _owners.Length - 1);

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var (handle, place) = _owners[middle];

            if (handle == owner.Packed)
            {
                return _publisher.Frees(_changes[place], index);
            }

            (low, high) = handle > owner.Packed ? (low, middle - 1) : (middle + 1, high);
        }

        return false;
    }

    // The change at the place given among the claims of each unique index of its entity type in which it sets
    // a value with no null part.
    private void AddClaims(in PreparedChange change, int place)
    {
        var indexes = change.Table.Indexes;
        var root = new RootReader(_arena, change.Table.Layout!, change.Root);

        for (var index = 0; index < indexes.Length; index++)
        {
            if (!indexes[index].IsUnique || !_publisher.SetsValue(change, index) || indexes[index].Layout.HasNull(root))
            {
                continue;
            }

            PlacesOf(change.Table, index).Add(place);
        }
    }

    // The places of the claims of the unique index at the place given among the indexes of the table, made
    // empty at its first claim; an entity type of a batch has few unique indexes, so they are looked for in
    // a list.
    private List<int> PlacesOf(EntityTable table, int index)
    {
        foreach (var claim in _claims)
        {
            if (ReferenceEquals(claim.Table, table) && claim.Index == index)
            {
                return claim.Places;
            }
        }

        var places = new List<int>();

        _claims.Add((table, index, places));

        return places;
    }

    // The order of the claims of one unique index: the order of the index applied to the new roots of the
    // changes at the places compared.
    private readonly struct ClaimOrder(IndexOrder order, Arena arena, RootLayout layout, PreparedChange[] changes) : IComparer<int>
    {
        public int Compare(int left, int right) =>
            order.CompareKeys(new RootReader(arena, layout, changes[left].Root), new RootReader(arena, layout, changes[right].Root));
    }
}
