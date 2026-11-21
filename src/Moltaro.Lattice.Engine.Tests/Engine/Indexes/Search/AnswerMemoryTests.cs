using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using Moltaro.Lattice.TestSupport.ApplicationCode;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// What FindAsync counts of its answer against the memory limit of a read (LatticeDatabaseOptions.ReadMemoryLimit;
// docs/decisions/queries.md, Q07.1.1), in the bytes the contract does not state: until it returns, everything
// the search holds, checked at every moment it grows: the keys it remembers, the list of its answer by its
// capacity, and each entity, checked when it is added, by everything stored for it: its root block and every
// value kept outside it, by their stored lengths, a text of Dictionary storage by the bytes of its entry once
// for each entity that refers to it. An answer of one entity whose step saw the end counts nothing; from the
// step that makes what a walk remembers, everything counts, the first entity included. So a limit of exactly
// what an answer of n entities holds lets FindAsync return them, and a limit one byte less fails it with
// ReadMemory. And a cancellation is reported before a limit: the token is looked at before the limit at every
// place a limit is looked at, so a token cancelled by the code of the application while an entity is built,
// or by another thread while a round of a step is inside, ends the search in OperationCanceledException, also
// when the check after it would exceed the limit. A round is held at a root it takes through the switch of the
// engine (EngineDatabase.ArmReadSwitch), which cancels the token there, as another thread could.
//
// The figures, by the rules: a search by the full key of ByNumber stands at no entry, so it holds none; its
// keys take KeysBytes while it remembers 12 keys or fewer; the list of its answer takes 8 bytes a reference of
// its capacity, which List<T> makes 4 at its first entity and doubles when it is full; an entity of Counted
// (IndexSchema) stores its root block alone, IndexSchema.CountedFixedSize, and one of Ranked what RankedBytes
// computes from its layout.
public sealed class AnswerMemoryTests
{
    // The keys a search remembers while it holds 12 or fewer Int64 keys (ReturnedKeys): the 16 places of the
    // first table, one word of 8 bytes each, of which three quarters are taken at most, and the reference of
    // 8 bytes of their one page.
    private const long KeysBytes = (16 * sizeof(long)) + sizeof(long);

    // The entities of Ranked of the tests of the storage forms, and the Login each has, which lies inline.
    private const int RankedRows = 4;

    private const string Login = "login";

    // The entities of HookedEntity of the tests of a cancellation, and what each stores: its root block, the
    // block header of 32 bytes, the Id, 8, the descriptor of Text, 16, and the one byte of its Text inline.
    private const int HookedRows = 6;

    private const long HookedBytes = 32 + 8 + 16 + 1;

    // HookedEntity, whose set accessor of Text runs the code a test gives, with the index ByText.
    private static readonly LatticeSchema HookedSchema = LatticeSchema.Compile($$"""
        {
          "StringEncoding": "Utf8",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Hooked", "ClrType": "{{typeof(HookedEntity).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2] } ]
            }
          ]
        }
        """);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Every_entity_of_the_answer_of_FindAsync_counts_against_the_memory_limit_when_it_is_added(int entities)
    {
        var holds = KeysBytes + ListBytes(entities) + (entities * (long)IndexSchema.CountedFixedSize);

        Assert.Equal(entities, (await FindCountedAsync(holds, entities)).Count);
        ExpectReadMemory(await Record.ExceptionAsync(() => FindCountedAsync(holds - 1, entities)));
    }

    // Every storage form of a value, in four entities of Ranked of one Number: a Note that is null, inline, in
    // a block of its own or in chunks, beside a Login inline and a Region of Dictionary storage, whose entry
    // of the text table, in one block or in chunks, the four refer to.
    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, 1)]
    [InlineData(40, 1)]
    [InlineData(2_000, 1_000)]
    [InlineData(50_000, 1)]
    [InlineData(100_000, 70_000)]
    public async Task Entity_of_the_answer_of_FindAsync_counts_its_root_block_and_every_value_kept_outside_it(int? note, int region)
    {
        var holds = KeysBytes + ListBytes(RankedRows) + (RankedRows * RankedBytes(note, region));

        Assert.Equal(RankedRows, (await FindRankedAsync(holds, note, region)).Count);
        ExpectReadMemory(await Record.ExceptionAsync(() => FindRankedAsync(holds - 1, note, region)));
    }

    // What an entity of Ranked stores, by its layout, in UTF-8, one byte a character of these texts: its root
    // block, the fixed part, IndexSchema.RankedFixedSize, with the values that lie inline after it, the Login
    // and a Note of ValueBlocks.InlineLimit bytes or fewer; every value kept outside it by its encoded bytes,
    // a Note in a block of its own or in chunks and the Region in the entry of the text table that holds it;
    // and for a value in chunks the 16 bytes its directory holds for each chunk. A value counts its bytes
    // wherever it lies, so only its directory depends on where.
    private static long RankedBytes(int? note, int region)
    {
        var bytes = (long)IndexSchema.RankedFixedSize + Login.Length + region + DirectoryBytes(region);

        if (note is { } length)
        {
            bytes += length + DirectoryBytes(length);
        }

        return bytes;
    }

    // The bytes of the directory of a value of the length given: one descriptor for each chunk of a value
    // longer than one block holds, none for a value in one block or inline.
    private static long DirectoryBytes(int length) =>
        length <= ValueBlocks.SingleBlockLimit
            ? 0
            : (long)((length + ValueBlocks.ChunkPayload - 1) / ValueBlocks.ChunkPayload) * ValueBlocks.DescriptorSize;

    // FindAsync of the Number of the entities of Ranked, with a Note of the length given or none and a Region
    // of the length given, in a database under the memory limit given.
    private static async Task<IReadOnlyList<IndexSchema.Ranked>> FindRankedAsync(long limit, int? note, int region)
    {
        await using var database = await OpenAsync(IndexSchema.Utf8, limit);
        var ranked = database.Set<IndexSchema.Ranked>();

        for (var id = 1L; id <= RankedRows; id++)
        {
            await ranked.InsertAsync(new IndexSchema.Ranked
            {
                Id = id,
                Number = 1,
                Login = Login,
                Region = new string('r', region),
                Note = note is { } length ? new string('n', length) : null,
            });
        }

        return (await ranked.Index("ByNumber").FindAsync(IndexKey.Equal(1L), new LatticeFindOptions())).Items;
    }

    // A set accessor of the class cancels the token while the entity given is built, under a limit one byte
    // less than what the answer holds once that entity is added: the check after the build exceeds it.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task FindAsync_cancelled_while_an_entity_is_built_ends_in_cancellation_under_a_limit_its_next_check_exceeds(int entity)
    {
        var limit = KeysBytes + ListBytes(entity) + (entity * HookedBytes) - 1;

        await using var database = await OpenAsync(HookedSchema, limit);
        var hooked = database.Set<HookedEntity>();

        for (var id = 1L; id <= HookedRows; id++)
        {
            await hooked.InsertAsync(new HookedEntity { Id = id, Text = "t" });
        }

        using var cancellation = new CancellationTokenSource();
        var built = 0;

        void CancelAtTheEntity()
        {
            if (++built == entity)
            {
                cancellation.Cancel();
            }
            else
            {
                HookedEntity.ArmSetText(CancelAtTheEntity);
            }
        }

        HookedEntity.ArmSetText(CancelAtTheEntity);

        var failure = await Record.ExceptionAsync(async () =>
            await hooked.Index("ByText").FindAsync(IndexKey.Equal("t"), new LatticeFindOptions(), cancellation.Token));

        Assert.True(failure is OperationCanceledException, $"The search ended in {failure?.GetType().Name}: {failure?.Message}");
    }

    // The token is cancelled inside the round that adds the second entity of the answer, at its root, past the
    // match an Offset skips, in either direction, under a limit one byte less than the answer holds once that
    // entity is added: without the cancellation the search fails there with ReadMemory.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public async Task FindAsync_cancelled_inside_a_round_ends_in_cancellation_under_a_limit_the_next_entity_exceeds(bool reverse, int offset)
    {
        var limit = KeysBytes + ListBytes(2) + (2 * (long)IndexSchema.CountedFixedSize) - 1;

        ExpectReadMemory(await Record.ExceptionAsync(() => FindCancelledAtAsync(limit, reverse, offset, cancels: false)));

        var failure = await Record.ExceptionAsync(() => FindCancelledAtAsync(limit, reverse, offset, cancels: true));

        Assert.True(failure is OperationCanceledException, $"The search ended in {failure?.GetType().Name}: {failure?.Message}");
    }

    // FindAsync of the four entities of Counted of one Number, in the direction and with the Offset given,
    // under the memory limit given, with the token cancelled at the first root of the second entity it would
    // return, when it cancels: the roots it takes are counted by their identities.
    private static async Task FindCancelledAtAsync(long limit, bool reverse, int offset, bool cancels)
    {
        await using var database = (EngineDatabase)await OpenAsync(IndexSchema.Utf8, limit);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= 4; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 7 });
        }

        using var cancellation = new CancellationTokenSource();
        var taken = new List<RootHandle>();

        void CancelAtTheSecondEntity(RootHandle root)
        {
            if (!taken.Contains(root))
            {
                taken.Add(root);
            }

            if (taken.Count == offset + 2)
            {
                cancellation.Cancel();
            }
            else
            {
                database.ArmReadSwitch(CancelAtTheSecondEntity);
            }
        }

        if (cancels)
        {
            database.ArmReadSwitch(CancelAtTheSecondEntity);
        }

        await counted.Index("ByNumber").FindAsync(IndexKey.Equal(7L), new LatticeFindOptions { Reverse = reverse, Offset = offset }, cancellation.Token);
    }

    // FindAsync of the key of ByNumber that the number of entities of Counted given have, in a database
    // under the memory limit given.
    private static async Task<IReadOnlyList<IndexSchema.Counted>> FindCountedAsync(long limit, int entities)
    {
        await using var database = await OpenAsync(IndexSchema.Utf8, limit);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= entities; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 7 });
        }

        return (await counted.Index("ByNumber").FindAsync(IndexKey.Equal(7L), new LatticeFindOptions())).Items;
    }

    private static ValueTask<ILatticeDatabase> OpenAsync(LatticeSchema schema, long limit) =>
        LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ReadMemoryLimit = limit,
        });

    // The bytes of the list of an answer of the number of entities given: 8 a reference of its capacity.
    private static long ListBytes(int entities)
    {
        var capacity = 4;

        while (capacity < entities)
        {
            capacity *= 2;
        }

        return capacity * (long)IntPtr.Size;
    }

    private static void ExpectReadMemory(Exception? failure)
    {
        var exception = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
        Assert.Equal(LatticeLimitEnum.ReadMemory, exception.Limit);
    }
}
