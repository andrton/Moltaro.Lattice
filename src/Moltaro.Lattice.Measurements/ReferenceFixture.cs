using Moltaro.Lattice.Access;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements;

// The reference fixture: the entities every measurement of the engine stores, so that every sprint
// measures the same entities. The milestone contract defines it ("Reproducible first fixture"): the
// entity types User, Group, Role and Setting with their counts, field types, text formats and
// indexes, 121,000 roots in all, generated from seed 42 with stable numeric identities from 1. Its
// text is synthetic; no private data is needed. No field is nullable, and an index is unique only
// where the table of the milestone contract says so. What that table does not give, such as the
// names it leaves open, the identities and the synthetic value sets, is this class's own: the
// descriptor below names them, and FixtureRecords says how the values are drawn.
//
// It has two forms. The full form is the one of the milestone contract, with its collections, its
// Dictionary strings and its unique indexes; the gates are judged on it only. The scalar form has the
// same entity types and keeps every index, without the collection fields and with every String
// Plain. A sprint before S19 measures the scalar form, because the Set of a role and the Map of a
// setting cannot be stored yet (its Dictionary strings can, from S13, and the Lists of its users and
// groups from S18, which root-bytes and full-form-users measure beside the scalar form), and before S16
// it measures that form without its Unique marks, because an entity type with a unique index cannot be
// written yet. A brief that says "the fixture's users" means the form its sprint can store.
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
    public static string SchemaJson => Descriptor(collections: true, unique: true);

    // The schema descriptor of the scalar form, as JSON: the entity types and every index of
    // SchemaJson, without the collection fields and with every String Plain. The indexes that the
    // full form marks Unique are unique only when unique is true.
    public static string ScalarSchemaJson(bool unique) => Descriptor(collections: false, unique);

    // The 121,000 records of the fixture for a schema compiled from SchemaJson or from
    // ScalarSchemaJson: for the scalar form without the collection fields. The same records in the
    // same order on every run. A schema that is neither of the two forms is refused with
    // ArgumentException, at the call.
    //
    // A schema is a form when it has the fingerprint of one, so that it stores, compares and
    // constrains its data as that form does, and has the names the records are made with.
    public static IEnumerable<LatticeRecord> Generate(LatticeSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var full = LatticeSchema.Compile(SchemaJson).Fingerprint;
        var scalar = LatticeSchema.Compile(ScalarSchemaJson(unique: false)).Fingerprint;
        var scalarUnique = LatticeSchema.Compile(ScalarSchemaJson(unique: true)).Fingerprint;

        if (schema.Fingerprint != full && schema.Fingerprint != scalar && schema.Fingerprint != scalarUnique)
        {
            throw new ArgumentException("The schema is neither the full nor the scalar form of the reference fixture.", nameof(schema));
        }

        return FixtureRecords.Generate(schema, collections: schema.Fingerprint == full, Seed);
    }

    // The SHA-256 of the values of the records, as lowercase hexadecimal, in an encoding that does
    // not depend on the order in which a Set or a Map enumerates its members. By it a run shows that
    // it generated the same fixture as every other. RecordDigest gives the encoding.
    public static string Digest(IEnumerable<LatticeRecord> records) => RecordDigest.Of(records);

    // The descriptor of either form. The identities of the entity types, the fields and the indexes,
    // and the names the table leaves open — Groups, Roles, Permissions and Options for the
    // collections, Key for the key of a setting, the names of the indexes — are the fixture's own.
    // A collection field comes last in its entity type, so that the scalar form keeps the identities
    // of every other field.
    private static string Descriptor(bool collections, bool unique)
    {
        var dictionary = collections ? """, "StringStorage": "Dictionary" """ : "";
        var uniqueMark = unique ? """, "Unique": true """ : "";
        var groups = collections ? """, { "Id": 7, "Name": "Groups", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Group" } }""" : "";
        var roles = collections ? """, { "Id": 3, "Name": "Roles", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Role" } }""" : "";
        var permissions = collections ? """, { "Id": 3, "Name": "Permissions", "Type": "Set", "Element": { "Type": "String", "StringStorage": "Dictionary" } }""" : "";
        var options = collections ? """, { "Id": 5, "Name": "Options", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "String" } }""" : "";

        return $$"""
            {
              "StringEncoding": "Utf8",
              "EntityTypes": [
                {
                  "Id": 1, "Name": "User",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Login", "Type": "String" },
                    { "Id": 3, "Name": "Email", "Type": "String" },
                    { "Id": 4, "Name": "DisplayName", "Type": "String" },
                    { "Id": 5, "Name": "Region", "Type": "String"{{dictionary}} },
                    { "Id": 6, "Name": "IsActive", "Type": "Boolean" }{{groups}}
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [
                    { "Id": 1, "Name": "ByLogin", "Fields": [2]{{uniqueMark}} },
                    { "Id": 2, "Name": "ByEmail", "Fields": [3] },
                    { "Id": 3, "Name": "ByRegionAndLogin", "Fields": [5, 2] }
                  ]
                },
                {
                  "Id": 2, "Name": "Group",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Name", "Type": "String" }{{roles}}
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [ { "Id": 1, "Name": "ByName", "Fields": [2] } ]
                },
                {
                  "Id": 3, "Name": "Role",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Name", "Type": "String" }{{permissions}}
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [ { "Id": 1, "Name": "ByName", "Fields": [2]{{uniqueMark}} } ]
                },
                {
                  "Id": 4, "Name": "Setting",
                  "Fields": [
                    { "Id": 1, "Name": "Key", "Type": "String" },
                    { "Id": 2, "Name": "Section", "Type": "String"{{dictionary}} },
                    { "Id": 3, "Name": "Name", "Type": "String" },
                    { "Id": 4, "Name": "Value", "Type": "String" }{{options}}
                  ],
                  "PrimaryKey": [1],
                  "Indexes": [ { "Id": 1, "Name": "BySectionAndName", "Fields": [2, 3] } ]
                }
              ]
            }
            """;
    }
}
