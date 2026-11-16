using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Enumeration;

// A pass over the entities of User, in the form a test names (EnumerationForms), as the key and the
// Name of each entity it returns.
internal static class UserPass
{
    public static IAsyncEnumerable<(long Id, string Name)> Of(ILatticeDatabase database, string form) =>
        EnumerationForms.Of(
            form,
            database.Set<User>(),
            database.Set("User"),
            user => (user.Id, user.Name),
            record => (record.Get<long>("Id"), record.Get<string>("Name")!));
}
