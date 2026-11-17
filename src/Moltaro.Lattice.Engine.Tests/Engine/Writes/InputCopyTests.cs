using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// No managed copy of an input (brief of Epic 1, sprint S09, "No managed copy of an input", the
// planner's answer to Q43 from the accepted decision "Stable write inputs until completion"): the
// engine takes the values of a write from the object or the record as the caller gave them and copies
// each once, into the arena, with no managed copy of a Binary value on the way. A write of an entity
// with a Binary value of 32 MiB therefore allocates less managed memory, in the whole process, than
// one copy of the value takes; through the class and by name, where the record holds the copy it made
// when the value was set, before the write. What the documentation promises besides — do not change
// the entity, its collections or its byte arrays until the write completes — is an obligation of the
// caller: what a write of an input changed meanwhile stores is not a behavior a test can state, beyond
// that a change after the write has completed changes nothing stored (DynamicSetTests of the scenario
// tests). A first write of each form takes the one-time costs of the path.
[Collection<WholeProcessAllocationCollection>]
public sealed class InputCopyTests
{
    private const int Size = 32 * 1024 * 1024;

    [Theory]
    [InlineData("by class")]
    [InlineData("by name")]
    public async Task Write_of_an_entity_with_a_Binary_value_of_32_MiB_allocates_no_managed_copy_of_the_value(string form)
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");

        // The one-time costs of the path, with a value in chunks.
        await WriteAsync(PieceSchema.Of(1, name: 10, data: 70_000));

        var data = new byte[Size];
        new Random(9).NextBytes(data);
        var piece = new PieceSchema.Piece { Id = 2, Name = "large", Data = data, Count = 2 };
        var record = PieceSchema.RecordOf(records.EntityType, 2, name: 5);
        record["Name"] = "large";
        record["Data"] = data;

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = form == "by class" ? await pieces.InsertAsync(piece) : await records.InsertAsync(record);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(result.Changed);
        Assert.True(allocated < Size, $"A write of an entity with a Binary value of {Size} bytes allocated {allocated} bytes of managed memory, a copy of the value or more.");
        Assert.True(data.AsSpan().SequenceEqual(pieces.Get(2L)?.Data), "The value stored differs from the one written.");

        ValueTask<LatticeWriteResult> WriteAsync(PieceSchema.Piece written) =>
            form == "by class"
                ? pieces.InsertAsync(written)
                : records.InsertAsync(PieceSchema.RecordOf(records.EntityType, written.Id, written.Name.Length, data: written.Data!.Length));
    }
}
