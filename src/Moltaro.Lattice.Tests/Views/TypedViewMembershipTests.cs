using System.Text.RegularExpressions;
using Moltaro.Lattice.TestSupport.Views;

namespace Moltaro.Lattice.Tests.Views;

public sealed class TypedViewMembershipTests
{
    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task A_view_generated_without_a_schema_file_can_read_inherited_fields_and_ignore_CLR_only_properties()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema());
        var items = database.Set<MembershipItem>();
        var records = database.Set("Item");
        await items.InsertAsync(Item());

        Assert.True(items.TryView(7, static (MembershipView view) => (view.Id, view.Home.Zip), out var byClass));
        Assert.True(records.TryView(7, static view => (view["Id"].Get<int>(), view["Home"].Owned["Zip"].Get<int>()), out var byName));
        Assert.Equal((7, 1234), byName);
        Assert.Equal(byName, byClass);
        Assert.Null(typeof(MembershipView).GetProperty(nameof(MembershipItem.DisplayOrder)));
        Assert.Null(typeof(MembershipView).GetProperty(nameof(MembershipItem.Home))!.PropertyType.GetProperty(nameof(MembershipAddress.DisplayOrder)));
    }

    [Theory(Skip = TypedViewSupport.NotImplemented)]
    [InlineData("DisplayOrder")]
    [InlineData("Home.DisplayOrder")]
    public async Task A_persisted_field_omitted_from_the_generated_view_is_refused_at_first_binding(string path)
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema(path));
        var items = database.Set<MembershipItem>();
        await items.InsertAsync(Item());
        var copy = Assert.IsType<MembershipItem>(items.Get(7));
        var expected = path == "DisplayOrder" ? 4 : 3;
        var persisted = path == "DisplayOrder" ? copy.DisplayOrder : copy.Home.DisplayOrder;
        Assert.Equal(expected, persisted);

        var error = Assert.Throws<LatticeException>(() => items.TryView(7,
            static (MembershipView _) => throw new InvalidOperationException("A view with an omitted persisted field ran its viewer."), out int _));
        AssertMissingField(path, error);
    }

    [Theory(Skip = TypedViewSupport.NotImplemented)]
    [InlineData("Home.DisplayOrder", "DisplayOrder")]
    [InlineData("DisplayOrder", "Home.DisplayOrder")]
    public async Task A_generated_projection_without_a_matching_schema_field_is_refused_at_first_binding(string persisted, string missing)
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema(persisted));
        var items = database.Set<MembershipItem>();
        await items.InsertAsync(Item());
        Assert.NotNull(items.Get(7));

        var session = new LatticeSession();
        var pending = items.ViewAsync<int, UnfilteredMembershipView, int>(7, session,
            static _ => throw new InvalidOperationException("A view with a missing schema field ran its viewer."));
        var error = await Assert.ThrowsAsync<LatticeException>(() => pending.AsTask());

        AssertMissingField(missing, error);
        Assert.Null(session.MinPosition);
    }

    private static void AssertMissingField(string path, LatticeException error)
    {
        Assert.Equal(LatticeErrorEnum.BindingFailed, error.Error);
        Assert.Matches($@"(?<![\w.]){Regex.Escape(path)}(?![\w.])", error.Message);
    }

    private static MembershipItem Item() => new()
    {
        Id = 7,
        DisplayOrder = 4,
        Home = new MembershipAddress { Zip = 1234, DisplayOrder = 3 },
    };

    private static LatticeSchema Schema(string? path = null)
    {
        const string displayOrder = """, { "Id": 3, "Name": "DisplayOrder", "Type": "Int32" }""";
        var rootFields = path == "DisplayOrder" ? displayOrder : "";
        var nestedFields = path == "Home.DisplayOrder" ? displayOrder : "";

        return LatticeSchema.Compile($$"""
            {
              "OwnedTypes": [
                {
                  "Id": 1, "Name": "Address", "ClrType": "{{typeof(MembershipAddress).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Zip", "Type": "Int32" }{{nestedFields}} ]
                }
              ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Item", "ClrType": "{{typeof(MembershipItem).FullName}}",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int32" },
                    { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address" }{{rootFields}}
                  ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
    }
}
