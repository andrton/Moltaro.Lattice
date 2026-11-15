namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// The entity type Item with two fields of one declaration, for the tests of the values of one type:
// Value, which is not nullable, and Maybe, which is. The declaration is the part of a field
// declaration after its name, written as ItemSchema writes it: 'Type': 'Int8', or
// 'Type': 'Enum', 'Enum': 'Code' together with the enum type Code.
internal static class TypedItem
{
    public static RootArena Open(string declaration, string enums = "", string encoding = "Utf8") =>
        RootArena.Of(
            $"{{ 'Id': 2, 'Name': 'Value', {declaration} }}, {{ 'Id': 3, 'Name': 'Maybe', {declaration}, 'Nullable': true }}",
            encoding,
            enums);

    // The declaration of a field of a scalar type, String or Binary, by the name of its type.
    public static RootArena OfType(string type) => Open($"'Type': '{type}'");
}
