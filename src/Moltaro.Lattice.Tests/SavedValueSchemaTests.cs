namespace Moltaro.Lattice.Tests;

// A schema with owned types, collections, references and indexes on fields inside owned values
// is saved and loaded with every declaration, through a stream and through a file, and the
// loaded schema has the fingerprints of the saved one.
public sealed class SavedValueSchemaTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Saving_to_a_stream_and_loading_keeps_every_declaration_and_both_fingerprints()
    {
        var compiled = ValueSchema.Compile();
        using var stream = new MemoryStream();

        compiled.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);

        AssertSameSchema(compiled, loaded);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Saving_to_a_file_and_loading_keeps_every_declaration_and_both_fingerprints()
    {
        var compiled = ValueSchema.Compile();
        var path = Path.Combine(Path.GetTempPath(), $"lattice-{Guid.NewGuid():N}.schema");
        try
        {
            compiled.Save(path);
            var loaded = LatticeSchema.Load(path);

            AssertSameSchema(compiled, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Loaded_schema_refers_to_its_own_owned_types()
    {
        using var stream = new MemoryStream();
        ValueSchema.Compile().Save(stream);
        stream.Position = 0;

        var loaded = LatticeSchema.Load(stream);

        var address = ValueSchema.OwnedType(loaded, "Address");
        Assert.Same(address, ValueSchema.Field(loaded, "Home").Declaration.OwnedType);
        Assert.Same(address, ValueSchema.Field(loaded, "Places").Declaration.Element!.OwnedType);
        Assert.True(address.TryGetField("Location", out var location));
        Assert.Same(ValueSchema.OwnedType(loaded, "Point"), location.Declaration.OwnedType);
        Assert.True(ValueSchema.User(loaded).TryGetIndex("ByHomeCity", out var byHomeCity));
        Assert.True(address.TryGetField("City", out var city));
        Assert.Same(city, Assert.Single(byHomeCity.Parts).Field);
    }

    private static void AssertSameSchema(LatticeSchema expected, LatticeSchema actual)
    {
        Assert.Equal(Describe(expected), Describe(actual));
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(
            expected.EntityTypes.Select(entityType => entityType.LayoutFingerprint),
            actual.EntityTypes.Select(entityType => entityType.LayoutFingerprint));

        // A few declarations spelt out, so that the comparison above is known to see them.
        var grid = ValueSchema.Field(actual, "Grid").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.DateOnly, grid.Value!.Value!.Element!.Type);
        Assert.Equal("Example.Address", ValueSchema.OwnedType(actual, "Address").ClrTypeName);
        Assert.True(ValueSchema.User(actual).TryGetIndex("ByHomeLat", out var byHomeLat));
        Assert.Equal([2, 4, 1], Assert.Single(byHomeLat.Parts).Path.Select(step => step.Id));
    }

    private static IEnumerable<string> Describe(LatticeSchema schema)
    {
        yield return $"version {schema.Version}, encoding {schema.StringEncoding}";

        foreach (var enumType in schema.EnumTypes)
        {
            yield return $"enum type {enumType.Id} {enumType.Name}: {enumType.UnderlyingType}, flags {enumType.IsFlags}, {string.Join(", ", enumType.Members.Select(member => $"{member.Name} = {member.Value}"))}";
        }

        foreach (var ownedType in schema.OwnedTypes)
        {
            yield return $"owned type {ownedType.Id} {ownedType.Name}: class {ownedType.ClrTypeName}";
            foreach (var field in ownedType.Fields)
            {
                yield return $"  field {field.Id} {field.Name}: {Describe(field.Declaration)}";
            }
        }

        foreach (var entityType in schema.EntityTypes)
        {
            yield return $"entity type {entityType.Id} {entityType.Name}: class {entityType.ClrTypeName}, key {entityType.Key.Id}";
            foreach (var field in entityType.Fields)
            {
                yield return $"  field {field.Id} {field.Name}: {Describe(field.Declaration)}";
            }

            foreach (var index in entityType.Indexes)
            {
                var parts = index.Parts.Select(part => string.Join(".", part.Path.Select(step => $"{step.Id} {step.Name}")));
                yield return $"  index {index.Id} {index.Name} on ({string.Join(", ", parts)}), unique {index.IsUnique}";
            }
        }
    }

    // A declaration with everything it holds, at every depth.
    private static string Describe(LatticeValueDeclaration? declaration) => declaration is null
        ? "none"
        : $"{declaration.Type}, nullable {declaration.IsNullable}, comparison {declaration.Comparison}, storage {declaration.StringStorage}, " +
          $"enum type {declaration.EnumType?.Id}, owned type {declaration.OwnedType?.Id}, refers to {declaration.ReferencedEntityTypeName}, value {declaration.ValueType}, " +
          $"element ({Describe(declaration.Element)}), key ({Describe(declaration.Key)}), value ({Describe(declaration.Value)})";
}
