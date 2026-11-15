using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Roots;

// The root table (milestone contract, "Native layout and allocation"; Q16, "Managed arrays of
// structs for control entries"; brief of S04, "A current handle" and "A new entry"). The control
// entries of the roots live in managed pages of 4,096 entries, and the table grows by adding a page
// without copying an entry. A handle is current from the moment Add gives it out until it is
// released: the entry of its slot has the handle's generation and the slot is neither free nor
// retired. The first generation of a slot is 1 and every reuse advances it, so the default handle
// and the handle of an earlier lifetime are never current. A slot released at generation
// uint.MaxValue is retired: never given out again and not counted.
public sealed class RootTableTests
{
    private const uint TypeId = 7;

    [Fact]
    public void Every_new_slot_starts_at_generation_1()
    {
        var table = new RootTable();

        var handles = Enumerable.Range(0, 10).Select(_ => table.Add(TypeId)).ToList();

        Assert.All(handles, handle => Assert.Equal(1u, handle.Generation));
        Assert.All(handles, handle => Assert.True(table.IsCurrent(handle)));
        Assert.Equal(10, handles.Select(handle => handle.Slot).Distinct().Count());
        Assert.Equal(10, table.Count);
    }

    [Fact]
    public void A_new_entry_is_prepared_with_its_generation_and_type_and_every_other_field_zero()
    {
        var table = new RootTable();

        var handle = table.Add(TypeId);

        AssertNewEntry(table.Entry(handle), handle, TypeId);
    }

    [Fact]
    public void The_default_handle_is_never_current()
    {
        var table = new RootTable();

        AssertNotCurrent(table, default);

        // Not even while every slot of the table holds a lifetime.
        AddUntilFull(table);

        AssertNotCurrent(table, default);
    }

    [Fact]
    public void A_handle_is_current_from_Add_until_it_is_released()
    {
        var table = new RootTable();
        var handle = table.Add(TypeId);
        var other = table.Add(TypeId);

        Assert.True(table.IsCurrent(handle));

        table.Release(handle);

        AssertNotCurrent(table, handle);
        Assert.True(table.IsCurrent(other));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Release_leaves_the_entry_free()
    {
        var table = new RootTable();
        var handle = table.Add(TypeId);

        // The reference names the slot's entry in its page, whatever lifetime the slot holds.
        ref var entry = ref table.Entry(handle);

        table.Release(handle);

        Assert.Equal(RootStateEnum.Free, entry.State);
    }

    [Fact]
    public void Release_looks_at_neither_the_claim_count_nor_the_state()
    {
        var table = new RootTable();
        RootStateEnum[] states = [RootStateEnum.Prepared, RootStateEnum.Live, RootStateEnum.Deleted];

        foreach (var state in states)
        {
            var handle = table.Add(TypeId);
            ref var entry = ref table.Entry(handle);
            entry.State = state;
            entry.ClaimCount = 5;

            table.Release(handle);

            Assert.False(table.IsCurrent(handle));
        }

        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void A_handle_that_names_no_lifetime_is_not_current_and_is_refused()
    {
        var table = new RootTable();
        var handle = table.Add(TypeId);
        var slots = (uint)(table.Pages * RootTable.EntriesPerPage);

        // A slot of the table that holds no lifetime: every slot but the one just given out.
        var freeSlot = handle.Slot == 0 ? 1u : 0u;
        RootHandle[] strangers =
        [
            new(handle.Generation + 1, handle.Slot),
            new(0, handle.Slot),
            new(1, freeSlot),
            new(1, slots),
            new(1, uint.MaxValue),
            new(uint.MaxValue, uint.MaxValue),
        ];

        foreach (var stranger in strangers)
        {
            AssertNotCurrent(table, stranger);
        }

        Assert.True(table.IsCurrent(handle));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void A_released_slot_is_given_out_again_with_its_next_generation_and_the_old_handle_does_not_match()
    {
        var table = new RootTable();
        var handles = AddUntilFull(table);
        var pages = table.Pages;
        var old = handles[^1];

        for (var reuse = 0; reuse < 3; reuse++)
        {
            Scribble(ref table.Entry(old));
            table.Release(old);

            // The released slot is the only free one, so Add gives it out again instead of adding a
            // page, with the next generation.
            var renewed = table.Add(TypeId + 1);

            Assert.Equal(pages, table.Pages);
            Assert.Equal(old.Slot, renewed.Slot);
            Assert.Equal(old.Generation + 1, renewed.Generation);

            // The old handle does not reach the new lifetime, which starts like any other.
            Assert.NotEqual(old, renewed);
            AssertNotCurrent(table, old);
            Assert.True(table.IsCurrent(renewed));
            AssertNewEntry(table.Entry(renewed), renewed, TypeId + 1);

            old = renewed;
        }
    }

    [Fact]
    public void A_slot_released_at_the_last_generation_is_retired_and_never_given_out_again()
    {
        var table = new RootTable();
        var handles = AddUntilFull(table);
        var first = handles[0];

        // Four billion releases of one slot are out of reach, so the test writes the last generation
        // into the entry. The lifetime is then named with that generation.
        ref var entry = ref table.Entry(first);
        entry.Generation = uint.MaxValue;
        var last = new RootHandle(uint.MaxValue, first.Slot);

        Assert.True(table.IsCurrent(last));
        Assert.False(table.IsCurrent(first));

        var count = table.Count;
        var pages = table.Pages;

        table.Release(last);

        Assert.Equal(RootStateEnum.Retired, entry.State);
        AssertNotCurrent(table, last);
        Assert.Equal(count - 1, table.Count);

        // Every other slot holds a lifetime and the retired one is not free, so the next lifetime
        // takes a new page, and so do the ones after it until the table is full again.
        var next = new List<RootHandle> { table.Add(TypeId) };

        Assert.Equal(pages + 1, table.Pages);

        while (table.Count < table.Pages * RootTable.EntriesPerPage - 1)
        {
            next.Add(table.Add(TypeId));
        }

        Assert.DoesNotContain(next, handle => handle.Slot == first.Slot);
        Assert.Equal(RootStateEnum.Retired, entry.State);
    }

    [Fact]
    public void Count_tells_how_many_slots_hold_a_lifetime()
    {
        var table = new RootTable();

        Assert.Equal(0, table.Count);

        var handles = Enumerable.Range(0, 5).Select(_ => table.Add(TypeId)).ToList();

        Assert.Equal(5, table.Count);

        table.Release(handles[1]);
        table.Release(handles[3]);

        Assert.Equal(3, table.Count);

        table.Add(TypeId);

        Assert.Equal(4, table.Count);
    }

    [Fact]
    public void The_table_adds_a_page_of_4096_entries_only_when_every_slot_holds_a_lifetime()
    {
        var table = new RootTable();
        table.Add(TypeId);
        var pages = table.Pages;

        Assert.Equal(4096, RootTable.EntriesPerPage);

        while (table.Count < pages * RootTable.EntriesPerPage)
        {
            table.Add(TypeId);

            Assert.Equal(pages, table.Pages);
        }

        table.Add(TypeId);

        Assert.Equal(pages + 1, table.Pages);
        Assert.Equal(pages * RootTable.EntriesPerPage + 1, table.Count);
    }

    [Fact]
    public void Adding_a_page_copies_no_entry_and_a_reference_taken_before_names_the_same_entry_after()
    {
        var table = new RootTable();
        var handles = AddUntilFull(table);

        for (var index = 0; index < handles.Count; index++)
        {
            table.Entry(handles[index]).StoredRootId = 1_000 + index;
        }

        ref var first = ref table.Entry(handles[0]);
        ref var last = ref table.Entry(handles[^1]);
        var pages = table.Pages;

        // Two pages are added, one after the other.
        handles.Add(table.Add(TypeId));
        handles.AddRange(AddUntilFull(table));
        handles.Add(table.Add(TypeId));

        Assert.Equal(pages + 2, table.Pages);

        // The references taken before still name the entries of those lifetimes, and what was
        // written stays where it was written.
        Assert.True(Unsafe.AreSame(ref first, ref table.Entry(handles[0])));
        Assert.True(Unsafe.AreSame(ref last, ref table.Entry(handles[pages * RootTable.EntriesPerPage - 1])));

        first.ClaimCount = 3;
        Assert.Equal(3, table.Entry(handles[0]).ClaimCount);

        table.Entry(handles[0]).LastMutationSequence = 99;
        Assert.Equal(99ul, first.LastMutationSequence);

        for (var index = 0; index < pages * RootTable.EntriesPerPage; index++)
        {
            Assert.Equal(1_000 + index, table.Entry(handles[index]).StoredRootId);
        }
    }

    // Adds lifetimes until every slot of the table holds one, and returns their handles. The table
    // adds a page only when no slot is free, so afterwards the next Add needs a new page.
    private static List<RootHandle> AddUntilFull(RootTable table)
    {
        var handles = new List<RootHandle> { table.Add(TypeId) };

        while (table.Count < table.Pages * RootTable.EntriesPerPage)
        {
            handles.Add(table.Add(TypeId));
        }

        return handles;
    }

    // The handle is not current, and Entry and Release refuse it without changing the count.
    private static void AssertNotCurrent(RootTable table, RootHandle handle)
    {
        var count = table.Count;

        Assert.False(table.IsCurrent(handle));
        Assert.Throws<ArgumentException>(() => table.Entry(handle));
        Assert.Throws<ArgumentException>(() => table.Release(handle));
        Assert.Equal(count, table.Count);
    }

    private static void AssertNewEntry(RootEntry entry, RootHandle handle, uint typeId)
    {
        Assert.Equal(handle.Generation, entry.Generation);
        Assert.Equal(typeId, entry.TypeId);
        Assert.Equal(RootStateEnum.Prepared, entry.State);
        Assert.True(entry.Root.IsNull);
        Assert.Equal(0ul, entry.LastMutationSequence);
        Assert.True(entry.KeyDescriptor.IsNull);
        Assert.Equal(0L, entry.StoredRootId);
        Assert.Equal(0, entry.ClaimCount);
    }

    // Writes something other than zero into every field of the entry but its generation, which
    // names the lifetime.
    private static void Scribble(ref RootEntry entry)
    {
        entry.Root = new ArenaAddress(1, 64);
        entry.LastMutationSequence = 5;
        entry.KeyDescriptor = new ArenaAddress(1, 128);
        entry.StoredRootId = 9;
        entry.TypeId = 77;
        entry.State = RootStateEnum.Live;
        entry.ClaimCount = 2;
    }
}
