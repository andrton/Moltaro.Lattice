namespace Moltaro.Lattice.Tests.Schemas.DescriptorForm;

// A schema is immutable and can be shared by any number of databases and threads (LatticeSchema). So is
// every collection it shows: the entity types, the enum types and the owned types of the schema, the
// fields and the indexes of an entity type, the fields of an owned type, the members of an enum type, the
// parts of an index and the path of a part. A caller that casts one of them to an array, to IList<T> or
// to the IList of .NET and writes through the cast is refused, or writes nothing: the collection holds
// what it held. The schema is the one with a declaration of every kind (ValueSchema), as it is compiled
// and as it is loaded from its saved form.
public sealed class ImmutableSchemaTests
{
    public static IEnumerable<TheoryDataRow<string>> Forms => [new("compiled"), new("loaded")];

    [Theory]
    [MemberData(nameof(Forms))]
    public void No_collection_of_a_schema_can_be_written_through_a_cast(string form)
    {
        var schema = SchemaOf(form);
        var user = ValueSchema.User(schema);
        var address = ValueSchema.OwnedType(schema, "Address");
        var color = Assert.Single(schema.EnumTypes);
        var index = user.Indexes.Single(each => each.Name == "ByHomeLat");
        var part = Assert.Single(index.Parts);

        var written = new List<string>();

        WriteThroughCasts("LatticeSchema.EntityTypes", schema.EntityTypes, written);
        WriteThroughCasts("LatticeSchema.EnumTypes", schema.EnumTypes, written);
        WriteThroughCasts("LatticeSchema.OwnedTypes", schema.OwnedTypes, written);
        WriteThroughCasts("LatticeEntityType.Fields", user.Fields, written);
        WriteThroughCasts("LatticeEntityType.Indexes", user.Indexes, written);
        WriteThroughCasts("LatticeOwnedType.Fields", address.Fields, written);
        WriteThroughCasts("LatticeEnumType.Members", color.Members, written);
        WriteThroughCasts("LatticeIndex.Parts", index.Parts, written);
        WriteThroughCasts("LatticeIndexPart.Path", part.Path, written);

        Assert.True(written.Count == 0, $"Changed through a cast: {string.Join(", ", written)}.");
    }

    private static LatticeSchema SchemaOf(string form)
    {
        var compiled = ValueSchema.Compile();

        if (form == "compiled")
        {
            return compiled;
        }

        using var stream = new MemoryStream();

        compiled.Save(stream);
        stream.Position = 0;

        return LatticeSchema.Load(stream);
    }

    // Writes null into the first place of the collection through each cast that allows it, and clears
    // it through the one of ICollection<T>; each write has to be refused with NotSupportedException or
    // change nothing. Adds the name of the collection to those written when it changed.
    private static void WriteThroughCasts<T>(string name, IReadOnlyList<T> collection, List<string> written)
        where T : class
    {
        Assert.NotEmpty(collection);

        var before = collection.ToArray();

        Refused(() =>
        {
            if (collection is T[] array)
            {
                array[0] = null!;
            }
        });
        Refused(() =>
        {
            if (collection is IList<T> list)
            {
                list[0] = null!;
            }
        });
        Refused(() =>
        {
            if (collection is System.Collections.IList list)
            {
                list[0] = null;
            }
        });
        Refused(() =>
        {
            if (collection is ICollection<T> items)
            {
                items.Clear();
            }
        });

        if (!collection.SequenceEqual(before, ReferenceEqualityComparer.Instance))
        {
            written.Add(name);
        }
    }

    // Runs the write; a refusal is what the test expects of it.
    private static void Refused(Action write)
    {
        try
        {
            write();
        }
        catch (NotSupportedException)
        {
        }
    }
}
