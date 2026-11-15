using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements;

// The reference fixture: the entities every measurement of the engine stores, so that every sprint
// measures the same entities. The milestone contract defines it ("Reproducible first fixture"): the
// entity types User, Group, Role and Setting with their counts, field types, text formats and
// indexes, 121,000 roots in all, generated from seed 42 with stable numeric identities from 1. Its
// text is synthetic; no private data is needed. No field is nullable, and an index is unique only
// where the table of the milestone contract says so. What that table does not give, such as the
// names it leaves open, the identities and the synthetic value sets, is this class's own.
//
// It has two forms. The full form is the one of the milestone contract, with its collections, its
// Dictionary strings and its unique indexes; the gates are judged on it only. The scalar form has the
// same entity types and keeps every index, without the collection fields and with every String
// Plain. A sprint before S19 measures the scalar form, because the collections and Dictionary
// strings of the full form cannot be stored yet, and before S16 it measures that form without its
// Unique marks, because an entity type with a unique index cannot be written yet. A brief that says
// "the fixture's users" means the form its sprint can store.
//
// "The same on every run": every run generates the same records from the seed, in the same order
// and with the same values, and Digest gives the same result for them. That is why the records come
// from a generator of the program's own and not from System.Random, whose sequence for a seed is not
// promised across versions of .NET.
internal static class ReferenceFixture
{
    // The seed of the generator, as the milestone contract gives it.
    public const int Seed = 42;

    // The schema descriptor of the full form, as JSON: User, Group, Role and Setting with their
    // fields and indexes as the milestone contract gives them.
    public static string SchemaJson =>
        throw new NotSupportedException("The reference fixture of Epic 1, sprint S05 is not implemented yet.");

    // The schema descriptor of the scalar form, as JSON: the entity types and every index of
    // SchemaJson, without the collection fields and with every String Plain. The indexes that the
    // full form marks Unique are unique only when unique is true.
    public static string ScalarSchemaJson(bool unique) =>
        throw new NotSupportedException("The reference fixture of Epic 1, sprint S05 is not implemented yet.");

    // The 121,000 records of the fixture for a schema compiled from SchemaJson or from
    // ScalarSchemaJson: for the scalar form without the collection fields. The same records in the
    // same order on every run. A schema that is neither of the two forms is refused with
    // ArgumentException.
    public static IEnumerable<LatticeRecord> Generate(LatticeSchema schema) =>
        throw new NotSupportedException("The reference fixture of Epic 1, sprint S05 is not implemented yet.");

    // The SHA-256 of the values of the records, as lowercase hexadecimal, in an encoding that does
    // not depend on the order in which a Set or a Map enumerates its members. By it a run shows that
    // it generated the same fixture as every other.
    public static string Digest(IEnumerable<LatticeRecord> records) =>
        throw new NotSupportedException("The reference fixture of Epic 1, sprint S05 is not implemented yet.");
}
