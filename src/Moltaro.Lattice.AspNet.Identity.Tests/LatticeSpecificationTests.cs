using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.Test;

namespace Moltaro.Lattice.AspNet.Identity.Tests;

// Microsoft's specification tests for an Identity store, run against the Lattice stores.
// The tests themselves are in the Specification folder, as published; this class only
// tells them how to get a store, a user and a role.
//
// The stores are not implemented yet. The tests are inherited, so they cannot carry a
// Skip of their own: every one of them starts by asking for a test context, and that is
// where they are skipped, all with one reason. Remove the skip when the stores exist, and
// the tests have to pass.
public sealed class LatticeSpecificationTests : IdentitySpecificationTestBase<AppUser, AppRole>, IDisposable
{
    private const string StoresNotImplemented = "The Lattice Identity stores are not implemented yet.";

    private readonly List<ILatticeDatabase> _databases = [];

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }
    }

    // A test context is a database of its own, with the application's schema.
    protected override object CreateTestContext()
    {
        Assert.Skip(StoresNotImplemented);

        var database = LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = AppSchema.Compiled,
        });
        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        _databases.Add(database);
        return database;
    }

    protected override void AddUserStore(IServiceCollection services, object? context = null)
    {
        services.AddSingleton((ILatticeDatabase)context!);

        // Registers the user store and the role store together.
        new IdentityBuilder(typeof(AppUser), typeof(AppRole), services).AddLatticeStores();
    }

    protected override void AddRoleStore(IServiceCollection services, object? context = null)
    {
        // Registered together with the user store.
    }

    protected override void SetUserPasswordHash(AppUser user, string hashedPassword) =>
        user.PasswordHash = hashedPassword;

    protected override AppUser CreateTestUser(
        string namePrefix = "",
        string email = "",
        string phoneNumber = "",
        bool lockoutEnabled = false,
        DateTimeOffset? lockoutEnd = null,
        bool useNamePrefixAsUserName = false) =>
        new()
        {
            UserName = useNamePrefixAsUserName ? namePrefix : $"{namePrefix}{Guid.NewGuid()}",
            Email = email,
            PhoneNumber = phoneNumber,
            LockoutEnabled = lockoutEnabled,
            LockoutEnd = lockoutEnd,
        };

    protected override AppRole CreateTestRole(string roleNamePrefix = "", bool useRoleNamePrefixAsRoleName = false) =>
        new(useRoleNamePrefixAsRoleName ? roleNamePrefix : $"{roleNamePrefix}{Guid.NewGuid()}");

    protected override Expression<Func<AppUser, bool>> UserNameEqualsPredicate(string userName) =>
        user => user.UserName == userName;

    protected override Expression<Func<AppUser, bool>> UserNameStartsWithPredicate(string userName) =>
        user => user.UserName!.StartsWith(userName);

    protected override Expression<Func<AppRole, bool>> RoleNameEqualsPredicate(string roleName) =>
        role => role.Name == roleName;

    protected override Expression<Func<AppRole, bool>> RoleNameStartsWithPredicate(string roleName) =>
        role => role.Name!.StartsWith(roleName);
}
