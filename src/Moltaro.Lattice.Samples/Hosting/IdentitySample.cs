using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moltaro.Lattice.AspNet.Identity;
using Moltaro.Lattice.DependencyInjection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Hosting;

// ASP.NET Core Identity with its users and roles in a Lattice database. The application
// keeps using UserManager and RoleManager; only the registration of the stores differs.
//
// This shows the intended use. The stores are not implemented yet: the first operation
// below throws NotSupportedException, which Program.cs reports.
internal static class IdentitySample
{
    public static async Task RunAsync()
    {
        Console.WriteLine("== ASP.NET Core Identity on Lattice ==");

        var services = new ServiceCollection();
        services.AddLogging();

        // The schema belongs to the application. Here it is the default declaration of the
        // Identity data, bound to the application's user class. An application whose user
        // class has properties of its own, or which keeps other data in the same database,
        // saves this descriptor as its schema file, adds its fields and entity types there
        // and compiles that file instead.
        var schema = LatticeSchema.Compile(LatticeIdentitySchema.CreateDescriptor<Account, IdentityRole>());

        // Whatever the schema comes from, this tells at once whether the stores can use it.
        LatticeIdentitySchema.Validate<Account, IdentityRole>(schema);

        // A database of its own for the Identity data, registered under a name.
        services.AddLattice("identity", new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        // Identity as usual, with the Lattice stores where the Entity Framework stores would be.
        services
            .AddIdentityCore<Account>()
            .AddRoles<IdentityRole>()
            .AddLatticeStores("identity");

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredKeyedService<ILatticeDatabase>("identity").OpenAsync();

        // One unit of work, such as one web request.
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        // Register.
        var ada = new Account { UserName = "ada", Email = "ada@example.org" };
        var created = await users.CreateAsync(ada, "Correct-Horse-9");
        Console.WriteLine($"User created: {created.Succeeded}");

        // The same name again is refused.
        var again = await users.CreateAsync(new Account { UserName = "ADA" }, "Correct-Horse-9");
        Console.WriteLine($"Second user with the same name: {again.Succeeded} ({string.Join(", ", again.Errors.Select(error => error.Code))})");

        // Sign in: find the user and check the password.
        var found = await users.FindByNameAsync("ada");
        Console.WriteLine($"Password accepted: {found is not null && await users.CheckPasswordAsync(found, "Correct-Horse-9")}");

        // Roles.
        await roles.CreateAsync(new IdentityRole("Administrator"));
        await users.AddToRoleAsync(ada, "Administrator");
        Console.WriteLine($"Roles of ada: {string.Join(", ", await users.GetRolesAsync(ada))}");

        // An external login leads back to the user.
        await users.AddLoginAsync(ada, new UserLoginInfo("github", "42", "GitHub"));
        Console.WriteLine($"User of the GitHub account 42: {(await users.FindByLoginAsync("github", "42"))?.UserName}");

        Console.WriteLine();
    }

    // The application's user class: the standard one, or a class derived from it.
    private sealed class Account : IdentityUser
    {
    }
}
