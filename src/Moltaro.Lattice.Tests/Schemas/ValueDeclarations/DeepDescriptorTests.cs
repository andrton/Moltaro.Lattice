using System.Text;

namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// A descriptor is read however deep its text nests, as a saved form is: a declaration nested
// deeper than a declaration may be is refused with InvalidSchema and its field named, whatever
// its depth, and text nested very deep anywhere else is refused for what it holds. Neither
// exhausts the stack.
public sealed class DeepDescriptorTests
{
    [Theory]
    // Beyond the depth at which a JSON reader commonly stops, 64, and deep enough that a reader
    // that recursed over it would exhaust the stack.
    [InlineData(61)]
    [InlineData(20_000)]
    public void Declaration_nested_however_deep_is_refused_with_its_field_named(int lists)
    {
        var declaration = new StringBuilder();
        declaration.Insert(0, """{ "Type": "List", "Element": """, lists).Append("""{ "Type": "Int64" }""").Append('}', lists);

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Holder",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Deep", {{declaration.ToString()["{ ".Length..]}} ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Holder.Deep", exception.Message);
    }

    [Fact]
    public void Text_nested_very_deep_outside_a_declaration_is_refused_for_what_it_holds()
    {
        const int Depth = 20_000;
        var arrays = new StringBuilder().Append('[', Depth).Append(']', Depth).ToString();

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile($$"""{ "Enums": {{arrays}}, "EntityTypes": [] }"""));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("enum type", exception.Message);
    }
}
