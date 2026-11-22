using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// The owner of a value of a unique index whose key is a text longer than an entry copies, which its
// entries and separators share with the text entry of the root (docs/design/ordered-index.md, "Text
// keys"), is found by the comparison of texts wherever its entry lies, as the owner of a number is
// (OwnerAtLeafBoundaryTests): as the last entry of a leaf, as the first of the next, equal to a separator,
// and after the owner at a separator was removed while the separator stays, when the value is free and then
// taken once. Account of UniqueSchema, its logins 100 characters long, alike but for their last digits, and
// inserted in ascending order so that the entries fill full leaves; the tests read the tree through the
// test member of the engine (EngineDatabase.IndexNodes) and choose the values at its boundaries from it.
public sealed class OwnerAtLeafBoundaryOfTextTests
{
    private const int Entities = 2000;

    [Fact]
    public async Task Long_text_value_held_at_a_boundary_of_leaves_or_equal_to_a_separator_is_taken_and_one_freed_there_is_taken_once()
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var accounts = database.Set<UniqueSchema.Account>();

        for (var id = 1L; id <= Entities; id++)
        {
            await accounts.InsertAsync(UniqueSchema.Of(id, LoginOf(id)));
        }

        var (leaves, separators) = TreeOf(database);

        Assert.True(leaves.Count >= 3 && separators.Count >= 2, $"The premise: a tree of several leaves; it has {leaves.Count}.");

        var next = 100_000L;

        for (var leaf = 0; leaf + 1 < leaves.Count; leaf++)
        {
            await ExpectTakenAsync(accounts, next++, leaves[leaf][^1]);
            await ExpectTakenAsync(accounts, next++, leaves[leaf + 1][0]);
        }

        foreach (var separator in separators)
        {
            await ExpectTakenAsync(accounts, next++, separator);
        }

        // The first entry of the second leaf goes, and its separator stays: its value is free, and taken once.
        var freed = leaves[1][0];

        Assert.Contains(freed, separators);
        await accounts.DeleteAsync(IdOf(freed));
        database.DrainRetired();
        Assert.Contains(freed, TreeOf(database).Separators);

        await accounts.InsertAsync(UniqueSchema.Of(next, freed));
        await ExpectTakenAsync(accounts, next + 1, freed);
        Assert.Equal([next], UniqueSchema.OwnersOf(accounts, freed));
        Assert.Equal(Entities, accounts.Enumerate().Count());
    }

    // A login of 100 characters, alike but for its last five, the id.
    private static string LoginOf(long id) => new string('l', 95) + $"{id:D5}";

    private static long IdOf(string login) => long.Parse(login[95..], System.Globalization.CultureInfo.InvariantCulture);

    // An insert of another entity with the login given, and a replace of entity 1 onto it, are refused
    // naming ByLogin, and the entity of the id is not stored.
    private static async Task ExpectTakenAsync(ILatticeSet<UniqueSchema.Account> accounts, long id, string login)
    {
        await UniqueSchema.ExpectTakenAsync("ByLogin", accounts.InsertAsync(UniqueSchema.Of(id, login)).AsTask());

        if (login != LoginOf(1))
        {
            await UniqueSchema.ExpectTakenAsync("ByLogin", accounts.ReplaceAsync(UniqueSchema.Of(1, login)).AsTask());
        }

        Assert.Null(accounts.Get(id));
    }

    // The logins of each leaf of ByLogin in the order of the index, and the logins of the separators of every
    // inner node.
    private static (List<List<string>> Leaves, List<string> Separators) TreeOf(EngineDatabase database)
    {
        Assert.True(database.Schema.TryGetEntityType("Account", out var type));

        var nodes = database.IndexNodes(type, "ByLogin");
        var leaves = nodes.Where(node => node.Level == 0).Select(node => node.Items.Select(item => (string)item.Key[0]!).ToList()).ToList();
        var separators = nodes.Where(node => node.Level > 0).SelectMany(node => node.Items.Select(item => (string)item.Key[0]!)).ToList();

        return (leaves, separators);
    }
}
