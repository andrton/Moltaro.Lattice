using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Writes;

internal static class PartialUpdateSample
{
    public static async Task RunAsync()
    {
        Console.WriteLine("== Partial updates ==");

        var schema = LatticeSchema.Compile("""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Account",
                  "Fields": [
                    { "Id": 1, "Name": "Login", "Type": "String" },
                    { "Id": 2, "Name": "DisplayName", "Type": "String" },
                    { "Id": 3, "Name": "Tags", "Type": "Set",
                      "Element": { "Type": "String", "Comparison": "OrdinalIgnoreCase" } }
                  ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });
        var accounts = database.Set("Account");
        await accounts.InsertAsync(Account(accounts, "ada", "Ada"));

        var profile = accounts.CreateUpdate("ada");
        profile.Assign("DisplayName", "Ada Lovelace");
        var added = profile.SetAdd("Tags", "vip");
        await profile.CommitAsync();
        Console.WriteLine($"Display name: {accounts.Get("ada")!["DisplayName"]}. Added vip: {added.Changed}.");

        var onboarding = database.CreateWriteBatch();
        onboarding.Insert(Account(accounts, "grace", "Grace Hopper"));
        var tags = accounts.CreateUpdate("ada");
        var alreadyPresent = tags.SetAdd("Tags", "VIP");
        var addedRole = tags.SetAdd("Tags", "mentor");
        onboarding.Update(tags);
        await onboarding.CommitAsync();
        Console.WriteLine($"Added VIP: {alreadyPresent.Changed}. Added mentor: {addedRole.Changed}.");
        Console.WriteLine();
    }

    private static LatticeRecord Account(ILatticeSet accounts, string login, string displayName) =>
        new(accounts.EntityType)
        {
            ["Login"] = login,
            ["DisplayName"] = displayName,
            ["Tags"] = Array.Empty<string>(),
        };
}
