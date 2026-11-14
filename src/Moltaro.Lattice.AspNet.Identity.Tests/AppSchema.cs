using System.Text.Json.Nodes;

namespace Moltaro.Lattice.AspNet.Identity.Tests;

// The application's schema: app.schema.json, kept in the application and compiled by it.
// It started as the default Identity declaration and was edited by hand: the user and the
// role are bound to the application's classes under the application's names, the user has
// two more fields and an index, and the application's own entity type Tenant stands next
// to the Identity ones.
internal static class AppSchema
{
    public static readonly string Json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "app.schema.json"));

    public static readonly LatticeSchema Compiled = LatticeSchema.Compile(Json);

    // The application's schema with one mistake made in it, to see how it is reported.
    public static LatticeSchema CompileWith(Action<JsonArray> change)
    {
        var descriptor = JsonNode.Parse(Json)!;
        change(descriptor["Tables"]!.AsArray());
        return LatticeSchema.Compile(descriptor.ToJsonString());
    }

    public static JsonNode Table(this JsonArray tables, string name) =>
        tables.Single(table => table!["Name"]!.GetValue<string>() == name)!;

    public static JsonNode Named(this JsonNode table, string collection, string name) =>
        table[collection]!.AsArray().Single(item => item!["Name"]!.GetValue<string>() == name)!;
}
