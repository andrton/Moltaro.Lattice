using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples;

// The schema: compile the JSON descriptor, save the compiled form, load it again.
internal static class SchemaSample
{
    public static LatticeSchema Run()
    {
        Console.WriteLine("== Schema ==");

        // Compile: parse and validate the descriptor and resolve entity types, fields and keys.
        // This can be done once, for example at build or deployment time.
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "directory.schema.json"));
        var compiled = LatticeSchema.Compile(json);

        // Save the compiled schema to a file. Save(Stream) writes it to any stream.
        var path = Path.Combine(Path.GetTempPath(), "directory.schema.bin");
        compiled.Save(path);
        Console.WriteLine($"Compiled schema saved: {new FileInfo(path).Length} bytes");

        // At startup, load the compiled schema instead of compiling the JSON again.
        var schema = LatticeSchema.Load(path);
        File.Delete(path);

        // Everything about the entity types is known from here on.
        foreach (var entityType in schema.EntityTypes)
        {
            var fields = string.Join(", ", entityType.Fields.Select(field => $"{field.Name}: {field.Declaration.Type}{(field.Declaration.IsNullable ? "?" : "")}"));
            Console.WriteLine($"{entityType.Name} (key {entityType.Key.Name}, class {entityType.ClrTypeName ?? "none"}): {fields}");
        }

        Console.WriteLine();
        return schema;
    }
}
