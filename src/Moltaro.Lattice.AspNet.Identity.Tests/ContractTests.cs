using System.Text.Json.Nodes;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.AspNet.Identity.Tests;

// What is in place today: the default declaration of the Identity data, the check of a
// schema the application supplies, and the registration of the stores.
public sealed class ContractTests
{
    [Fact]
    public void Default_descriptor_declares_the_identity_entity_types()
    {
        var schema = LatticeSchema.Compile(LatticeIdentitySchema.CreateDescriptor<IdentityUser, IdentityRole>());

        Assert.Equal(
            [
                "IdentityUser", "IdentityRole", "IdentityUserRole", "IdentityUserClaim",
                "IdentityUserLogin", "IdentityUserToken", "IdentityRoleClaim",
            ],
            schema.EntityTypes.Select(entityType => entityType.Name));

        // The user and the role are bound to classes; the rest are records.
        Assert.True(schema.TryGetEntityType(typeof(IdentityUser), out var user));
        Assert.True(schema.TryGetEntityType(typeof(IdentityRole), out _));
        Assert.Equal(2, schema.EntityTypes.Count(entityType => entityType.ClrTypeName is not null));

        // What must be unique is declared unique, and the database keeps it.
        Assert.Equal(
            [
                "IdentityUser.ByNormalizedUserName", "IdentityRole.ByNormalizedName", "IdentityUserRole.ByUserAndRole",
                "IdentityUserLogin.ByProviderAndKey", "IdentityUserToken.ByUserProviderAndName",
            ],
            schema.EntityTypes.SelectMany(entityType => entityType.Indexes
                .Where(index => index.IsUnique)
                .Select(index => $"{entityType.Name}.{index.Name}")));

        // An email may be shared unless the application decides otherwise.
        Assert.True(user.TryGetIndex("ByNormalizedEmail", out var byEmail));
        Assert.False(byEmail.IsUnique);

        LatticeIdentitySchema.Validate<IdentityUser, IdentityRole>(schema);
    }

    [Fact]
    public void Default_descriptor_can_be_bound_to_the_application_classes()
    {
        var schema = LatticeSchema.Compile(LatticeIdentitySchema.CreateDescriptor<AppUser, AppRole>());

        Assert.True(schema.TryGetEntityType(typeof(AppUser), out var user));
        Assert.True(schema.TryGetEntityType(typeof(AppRole), out var role));
        Assert.Equal("IdentityUser", user.Name);
        Assert.Equal("IdentityRole", role.Name);
        LatticeIdentitySchema.Validate<AppUser, AppRole>(schema);

        // The default declares the standard fields only. A property the class adds is not
        // stored until the application declares a field for it in its own copy.
        Assert.False(user.TryGetField(nameof(AppUser.DisplayName), out _));

        // The schema is for these classes, not for others.
        ExpectInvalid(() => LatticeIdentitySchema.Validate<IdentityUser, IdentityRole>(schema), "Microsoft.AspNetCore.Identity.IdentityUser");
    }

    [Fact]
    public async Task Application_supplies_its_own_schema_with_its_own_fields_and_entity_types()
    {
        // app.schema.json: the default declaration, edited by the application.
        var schema = AppSchema.Compiled;
        LatticeIdentitySchema.Validate<AppUser, AppRole>(schema);

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        // The entity types carry the application's names and the application's fields ...
        var users = database.Set<AppUser>();
        Assert.Equal("User", users.EntityType.Name);
        Assert.True(users.EntityType.TryGetField(nameof(AppUser.DisplayName), out _));
        Assert.Equal("ByTenant", users.Index("ByTenant").Definition.Name);
        Assert.Equal("Role", database.Set<AppRole>().EntityType.Name);

        // ... and stand next to the application's own data in one database.
        var tenants = database.Set("Tenant");
        await tenants.InsertAsync(new LatticeRecord(tenants.EntityType) { ["Id"] = 1L, ["Name"] = "acme" });
        Assert.NotNull(tenants.Get(1L));
    }

    [Fact]
    public void Schema_that_lacks_what_the_stores_need_is_refused_with_a_list()
    {
        // An entity type is gone.
        ExpectInvalid(
            () => Validate(tables => tables.Remove(tables.Table("IdentityUserToken"))),
            "entity type 'IdentityUserToken' is missing");

        // A field is gone, or is declared differently.
        ExpectInvalid(
            () => Validate(tables => tables.Table("User")["Fields"]!.AsArray().Remove(tables.Table("User").Named("Fields", "PhoneNumber"))),
            "field 'User.PhoneNumber' is missing");
        ExpectInvalid(
            () => Validate(tables => tables.Table("IdentityUserClaim").Named("Fields", "ClaimValue")["Type"] = "Int32"),
            "field 'IdentityUserClaim.ClaimValue' must be a nullable String");

        // An index is gone, covers other fields, or no longer keeps values unique.
        ExpectInvalid(
            () => Validate(tables => tables.Table("IdentityUserClaim")["Indexes"]!.AsArray().Remove(tables.Table("IdentityUserClaim").Named("Indexes", "ByClaim"))),
            "index 'IdentityUserClaim.ByClaim' on (ClaimType, ClaimValue) is missing");
        ExpectInvalid(
            () => Validate(tables => tables.Table("IdentityUserRole").Named("Indexes", "ByUserAndRole")["Fields"] = new JsonArray(3, 2)),
            "index 'IdentityUserRole.ByUserAndRole' must be on (UserId, RoleId)");
        ExpectInvalid(
            () => Validate(tables => tables.Table("User").Named("Indexes", "ByNormalizedUserName")["Unique"] = false),
            "index 'User.ByNormalizedUserName' must be unique");

        // Everything found is reported at once.
        var exception = ExpectInvalid(
            () => Validate(tables =>
            {
                tables.Remove(tables.Table("IdentityRoleClaim"));
                tables.Table("Role").Named("Indexes", "ByNormalizedName")["Unique"] = false;
            }),
            "entity type 'IdentityRoleClaim' is missing");
        Assert.Contains("index 'Role.ByNormalizedName' must be unique", exception.Message);
    }

    [Fact]
    public void Application_may_make_the_schema_stricter()
    {
        // For example one account per email address.
        var schema = AppSchema.CompileWith(tables => tables.Table("User").Named("Indexes", "ByNormalizedEmail")["Unique"] = true);

        LatticeIdentitySchema.Validate<AppUser, AppRole>(schema);
    }

    [Fact]
    public async Task Stores_are_registered_and_report_what_they_will_support()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var users = scope.Users;

        Assert.True(users.SupportsUserPassword);
        Assert.True(users.SupportsUserEmail);
        Assert.True(users.SupportsUserPhoneNumber);
        Assert.True(users.SupportsUserSecurityStamp);
        Assert.True(users.SupportsUserLockout);
        Assert.True(users.SupportsUserTwoFactor);
        Assert.True(users.SupportsUserRole);
        Assert.True(users.SupportsUserClaim);
        Assert.True(users.SupportsUserLogin);
        Assert.True(users.SupportsUserAuthenticationTokens);
        Assert.True(users.SupportsUserAuthenticatorKey);
        Assert.True(users.SupportsUserTwoFactorRecoveryCodes);
        Assert.True(users.SupportsQueryableUsers);
        Assert.True(scope.Roles.SupportsRoleClaims);
        Assert.True(scope.Roles.SupportsQueryableRoles);

        // Passkeys are not part of the first version.
        Assert.False(users.SupportsUserPasskey);
    }

    [Fact]
    public async Task Stores_can_use_a_named_database()
    {
        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = AppSchema.Compiled,
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKeyedSingleton("identity", database);
        services.AddIdentityCore<AppUser>().AddRoles<AppRole>().AddLatticeStores("identity");
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IUserStore<AppUser>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRoleStore<AppRole>>());
    }

    [Fact]
    public async Task Stores_refuse_a_database_whose_schema_cannot_hold_the_identity_data()
    {
        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = AppSchema.CompileWith(tables => tables.Remove(tables.Table("IdentityUserLogin"))),
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(database);
        services.AddIdentityCore<AppUser>().AddRoles<AppRole>().AddLatticeStores();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // The mistake is reported when the store is asked for, not when data is half written.
        ExpectInvalid(() => scope.ServiceProvider.GetRequiredService<IUserStore<AppUser>>(), "entity type 'IdentityUserLogin' is missing");
        ExpectInvalid(() => scope.ServiceProvider.GetRequiredService<IRoleStore<AppRole>>(), "entity type 'IdentityUserLogin' is missing");
    }

    [Fact]
    public void Only_string_keys_are_supported()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddIdentityCore<IdentityUser<Guid>>().AddLatticeStores());
    }

    [Fact]
    public async Task Stores_are_not_implemented_yet()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();

        await Assert.ThrowsAsync<NotSupportedException>(() => scope.Users.FindByIdAsync("1"));
        await Assert.ThrowsAsync<NotSupportedException>(() => scope.Roles.FindByIdAsync("1"));
    }

    private static void Validate(Action<JsonArray> mistake) =>
        LatticeIdentitySchema.Validate<AppUser, AppRole>(AppSchema.CompileWith(mistake));

    private static LatticeException ExpectInvalid(Action action, string expectedProblem)
    {
        var exception = Assert.Throws<LatticeException>(action);
        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains(expectedProblem, exception.Message);
        return exception;
    }
}
