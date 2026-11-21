using Moltaro.Lattice.Access;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search.SearchAllocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// One object holds a search, with its key in it once, so that a search allocates that object and what it
// returns and nothing else (docs/design/ordered-index.md, "The search by a full key"). Measured as a read
// by key is, with GC.GetAllocatedBytesForCurrentThread, after a first search of the same form, in the test
// collection of the tests that measure managed allocation: Find of ByNumber of Counted (IndexSchema), 100
// entities, enumerated to its end, for a key one entity has and for a key none has.
//
// The figures are exact, so that one more object, 24 bytes at the least, or a larger object fails them;
// the constants say what each is made of. The arrays the copy of a class rents come back from the shared
// pools without an allocation after the first search, and no garbage collection, which may trim the pools,
// runs in the test collection. The figures are the same in a Debug and in a Release build, measured in
// each: the build changes the code, not the layout of an object, and the code allocates the same. They are
// the same on every job of CI, x64 and ARM64, Windows and Linux, because they are made of the layout of
// objects in a 64-bit process, which those share: every object has a header and a pointer to its type of
// 8 bytes each and a size that is a multiple of 8, and a reference and a 64-bit number take 8 bytes aligned
// to 8; no part has a type whose alignment differs between them, such as Int128. Nor does the order in
// which the runtime places fields change a size: in the object and in each value inside it, the fields
// smaller than 8 bytes take 8 together in any order, and the runtime puts no field into the padding of a
// value inside another. A 32-bit process, which no job runs, would change every figure.
[Collection<ManagedAllocationCollection>]
public sealed class SearchObjectAllocationTests
{
    // The object of a search through the class (Engine.Sets.IndexMatches), 328 bytes:
    // - 16 bytes that every object has, its header and the pointer to its type;
    // - the key, bound to the fields of the index, 232 bytes (IndexKey): four values of 32 bytes, each two
    //   64-bit words of bits, a reference to a text and the byte of its field type, padded to 8
    //   (Access.Values.IndexValue); two optional bounds of 48 bytes, each a value and a Boolean, 40 bytes,
    //   and the flag of the optional, padded to 8; and the count of its values and whether a factory made
    //   it, 8 bytes together;
    // - three references, 24 bytes: the operations of the set, the index and the current entity;
    // - the copy of a root the set makes, 24 bytes (ClassCopy: the copier and the two arrays it rents);
    // - where the search stands, 24 bytes (Entities.IndexSearch): the identity after the last entity
    //   returned, 8 bytes, and the sequence of an end a step saw, a nullable 64-bit number, 16;
    // - the state of the search, 4 bytes, and 4 of padding to a multiple of 8.
    private const long ClassSearch = 328;

    // The object of a search by name, 320 bytes: the same but for the copy of a root, which is a record's,
    // 16 bytes (RecordCopy: the entity type and the record).
    private const long NameSearch = 320;

    // What a search returns through the class, as a read by key does, 32 bytes: the Counted object, its 16
    // bytes and its two Int64.
    private const long ClassEntity = 32;

    // What a search returns by name, as a read by key does, 120 bytes: the record, 32 bytes, its 16 and the
    // references to its entity type and to its values; the array of its two values, 40 bytes, its 16, its
    // length padded to 8 and two references; and the boxed Int64 of each value, 24 bytes each.
    private const long NameEntity = 120;

    [Fact]
    public async Task Search_allocates_exactly_its_one_object_and_what_it_returns_through_a_class_and_by_name()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();
        var records = database.Set("Counted");

        for (var id = 1L; id <= 100; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
        }

        // The index objects are taken once, as an application keeps them.
        var byClass = counted.Index("ByNumber");
        var byName = records.Index("ByNumber");

        var classEntity = Measure(() => counted.Get(50L) is null ? 0 : 1, found: 1);
        var nameEntity = Measure(() => records.Get(50L) is null ? 0 : 1, found: 1);

        Assert.True(classEntity == ClassEntity, $"A read by key through the class allocated {classEntity} bytes, not the {ClassEntity} of its Counted object.");
        Assert.True(nameEntity == NameEntity, $"A read by key by name allocated {nameEntity} bytes, not the {NameEntity} of its record.");

        var classOne = Measure(() => Count(byClass.Find(IndexKey.Equal(50L))), found: 1);
        var classNone = Measure(() => Count(byClass.Find(IndexKey.Equal(101L))), found: 0);
        var nameOne = Measure(() => Count(byName.Find(IndexKey.Equal(50L))), found: 1);
        var nameNone = Measure(() => Count(byName.Find(IndexKey.Equal(101L))), found: 0);

        Assert.True(classOne == ClassSearch + ClassEntity, $"A search through the class that returns one entity allocated {classOne} bytes, not the {ClassSearch} of its object and the {ClassEntity} of the entity.");
        Assert.True(classNone == ClassSearch, $"A search through the class that finds nothing allocated {classNone} bytes, not the {ClassSearch} of its object.");
        Assert.True(nameOne == NameSearch + NameEntity, $"A search by name that returns one entity allocated {nameOne} bytes, not the {NameSearch} of its object and the {NameEntity} of the record.");
        Assert.True(nameNone == NameSearch, $"A search by name that finds nothing allocated {nameNone} bytes, not the {NameSearch} of its object.");
    }
}
