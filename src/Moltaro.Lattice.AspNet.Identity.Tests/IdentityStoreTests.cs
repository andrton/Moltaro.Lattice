using System.Security.Claims;

namespace Moltaro.Lattice.AspNet.Identity.Tests;

// What an application does through UserManager and RoleManager, on the Lattice stores.
//
// The stores are not implemented yet. These tests are written against the contract and
// skipped: remove their Skip when the stores exist, and they have to pass.
public sealed class IdentityStoreTests
{
    private const string StoresNotImplemented = "The Lattice Identity stores are not implemented yet.";
    private const string Password = "Correct-Horse-9";

    [Fact(Skip = StoresNotImplemented)]
    public async Task Created_user_is_found_by_id_by_name_and_by_email()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var user = new AppUser { UserName = "ada", Email = "ada@example.org" };

        Assert.True((await scope.Users.CreateAsync(user)).Succeeded);

        Assert.Equal("ada", (await scope.Users.FindByIdAsync(user.Id))?.UserName);
        Assert.Equal(user.Id, (await scope.Users.FindByNameAsync("ADA"))?.Id);
        Assert.Equal(user.Id, (await scope.Users.FindByEmailAsync("Ada@Example.org"))?.Id);
        Assert.Null(await scope.Users.FindByNameAsync("nobody"));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Password_is_checked_and_changed()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var user = new AppUser { UserName = "ada" };
        Assert.True((await scope.Users.CreateAsync(user, Password)).Succeeded);

        Assert.True(await scope.Users.CheckPasswordAsync(user, Password));
        Assert.False(await scope.Users.CheckPasswordAsync(user, "wrong"));

        Assert.True((await scope.Users.ChangePasswordAsync(user, Password, "Another-Horse-7")).Succeeded);

        // Another unit of work reads what was stored.
        await using var later = app.NewScope();
        var stored = await later.Users.FindByNameAsync("ada");
        Assert.True(await later.Users.CheckPasswordAsync(stored!, "Another-Horse-7"));
        Assert.False(await later.Users.CheckPasswordAsync(stored!, Password));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Two_users_cannot_have_the_same_name()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        Assert.True((await scope.Users.CreateAsync(new AppUser { UserName = "ada" })).Succeeded);

        // Names are compared in their normalized form.
        var second = await scope.Users.CreateAsync(new AppUser { UserName = "ADA" });

        Assert.False(second.Succeeded);
        Assert.Contains(second.Errors, error => error.Code == nameof(IdentityErrorDescriber.DuplicateUserName));
        Assert.Single(scope.Users.Users);
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Registering_one_name_from_many_callers_at_once_creates_one_user()
    {
        await using var app = await IdentityApp.StartAsync();

        // Each caller is its own unit of work, as concurrent web requests are.
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            await using var scope = app.NewScope();
            return await scope.Users.CreateAsync(new AppUser { UserName = "ada" });
        }));

        Assert.Single(results, result => result.Succeeded);

        await using var check = app.NewScope();
        Assert.Single(check.Users.Users);
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Update_based_on_an_outdated_copy_is_a_concurrency_failure()
    {
        await using var app = await IdentityApp.StartAsync();
        string userId;
        await using (var setup = app.NewScope())
        {
            var user = new AppUser { UserName = "ada" };
            await setup.Users.CreateAsync(user);
            userId = user.Id;
        }

        // Two units of work read the same user.
        await using var first = app.NewScope();
        await using var second = app.NewScope();
        var firstCopy = (await first.Users.FindByIdAsync(userId))!;
        var secondCopy = (await second.Users.FindByIdAsync(userId))!;

        firstCopy.PhoneNumber = "111";
        Assert.True((await first.Users.UpdateAsync(firstCopy)).Succeeded);

        // The second one is told, and its change is not written.
        secondCopy.PhoneNumber = "222";
        var result = await second.Users.UpdateAsync(secondCopy);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure));

        await using var check = app.NewScope();
        Assert.Equal("111", (await check.Users.FindByIdAsync(userId))?.PhoneNumber);
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Renaming_a_user_frees_the_old_name_and_takes_the_new_one()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var user = new AppUser { UserName = "ada" };
        await scope.Users.CreateAsync(user);

        Assert.True((await scope.Users.SetUserNameAsync(user, "lovelace")).Succeeded);

        Assert.Null(await scope.Users.FindByNameAsync("ada"));
        Assert.Equal(user.Id, (await scope.Users.FindByNameAsync("lovelace"))?.Id);

        // The old name can be taken by somebody else, the new one cannot.
        Assert.True((await scope.Users.CreateAsync(new AppUser { UserName = "ada" })).Succeeded);
        Assert.False((await scope.Users.CreateAsync(new AppUser { UserName = "lovelace" })).Succeeded);
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Failed_sign_ins_are_counted_and_lock_the_user()
    {
        await using var app = await IdentityApp.StartAsync(options => options.Lockout.MaxFailedAccessAttempts = 3);
        await using var scope = app.NewScope();
        var user = new AppUser { UserName = "ada" };
        await scope.Users.CreateAsync(user);
        await scope.Users.SetLockoutEnabledAsync(user, true);

        await scope.Users.AccessFailedAsync(user);
        await scope.Users.AccessFailedAsync(user);
        Assert.Equal(2, await scope.Users.GetAccessFailedCountAsync(user));
        Assert.False(await scope.Users.IsLockedOutAsync(user));

        await scope.Users.AccessFailedAsync(user);

        // The third failure locks the user, and that is what the next unit of work reads.
        await using var later = app.NewScope();
        var stored = (await later.Users.FindByNameAsync("ada"))!;
        Assert.True(await later.Users.IsLockedOutAsync(stored));
        Assert.NotNull(await later.Users.GetLockoutEndDateAsync(stored));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task User_gets_roles_and_a_role_lists_its_users()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        await scope.Roles.CreateAsync(new AppRole("Administrator"));
        await scope.Roles.CreateAsync(new AppRole("Auditor"));
        var ada = new AppUser { UserName = "ada" };
        var grace = new AppUser { UserName = "grace" };
        await scope.Users.CreateAsync(ada);
        await scope.Users.CreateAsync(grace);

        await scope.Users.AddToRolesAsync(ada, ["Administrator", "Auditor"]);
        await scope.Users.AddToRoleAsync(grace, "Auditor");

        Assert.Equal(["Administrator", "Auditor"], (await scope.Users.GetRolesAsync(ada)).Order());
        Assert.True(await scope.Users.IsInRoleAsync(grace, "auditor"));
        Assert.False(await scope.Users.IsInRoleAsync(grace, "Administrator"));
        Assert.Equal(["ada", "grace"], (await scope.Users.GetUsersInRoleAsync("Auditor")).Select(user => user.UserName).Order());

        // A user is in a role once.
        Assert.False((await scope.Users.AddToRoleAsync(ada, "Auditor")).Succeeded);

        await scope.Users.RemoveFromRoleAsync(ada, "Auditor");
        Assert.Equal(["Administrator"], await scope.Users.GetRolesAsync(ada));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task User_gets_claims_and_users_are_found_by_a_claim()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var ada = new AppUser { UserName = "ada" };
        var grace = new AppUser { UserName = "grace" };
        await scope.Users.CreateAsync(ada);
        await scope.Users.CreateAsync(grace);
        var department = new Claim("department", "research");

        await scope.Users.AddClaimsAsync(ada, [department, new Claim("level", "3")]);
        await scope.Users.AddClaimAsync(grace, department);

        Assert.Equal(["department=research", "level=3"], (await scope.Users.GetClaimsAsync(ada)).Select(claim => $"{claim.Type}={claim.Value}").Order());
        Assert.Equal(["ada", "grace"], (await scope.Users.GetUsersForClaimAsync(department)).Select(user => user.UserName).Order());

        await scope.Users.ReplaceClaimAsync(ada, new Claim("level", "3"), new Claim("level", "4"));
        await scope.Users.RemoveClaimAsync(ada, department);

        Assert.Equal(["level=4"], (await scope.Users.GetClaimsAsync(ada)).Select(claim => $"{claim.Type}={claim.Value}"));
        Assert.Equal(["grace"], (await scope.Users.GetUsersForClaimAsync(department)).Select(user => user.UserName));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task External_login_leads_to_its_user()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var user = new AppUser { UserName = "ada" };
        await scope.Users.CreateAsync(user);

        Assert.True((await scope.Users.AddLoginAsync(user, new UserLoginInfo("github", "42", "GitHub"))).Succeeded);

        Assert.Equal(user.Id, (await scope.Users.FindByLoginAsync("github", "42"))?.Id);
        Assert.Null(await scope.Users.FindByLoginAsync("github", "43"));
        Assert.Equal("GitHub", Assert.Single(await scope.Users.GetLoginsAsync(user)).ProviderDisplayName);

        // One external account belongs to one user.
        var other = new AppUser { UserName = "grace" };
        await scope.Users.CreateAsync(other);
        Assert.False((await scope.Users.AddLoginAsync(other, new UserLoginInfo("github", "42", "GitHub"))).Succeeded);

        await scope.Users.RemoveLoginAsync(user, "github", "42");
        Assert.Null(await scope.Users.FindByLoginAsync("github", "42"));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Tokens_authenticator_key_and_recovery_codes_are_kept_per_user()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var user = new AppUser { UserName = "ada" };
        await scope.Users.CreateAsync(user);

        await scope.Users.SetAuthenticationTokenAsync(user, "github", "access_token", "secret");
        await scope.Users.ResetAuthenticatorKeyAsync(user);
        var codes = (await scope.Users.GenerateNewTwoFactorRecoveryCodesAsync(user, 3))!.ToList();

        await using var later = app.NewScope();
        var stored = (await later.Users.FindByNameAsync("ada"))!;
        Assert.Equal("secret", await later.Users.GetAuthenticationTokenAsync(stored, "github", "access_token"));
        Assert.NotNull(await later.Users.GetAuthenticatorKeyAsync(stored));
        Assert.Equal(3, await later.Users.CountRecoveryCodesAsync(stored));

        // A recovery code works once.
        Assert.True((await later.Users.RedeemTwoFactorRecoveryCodeAsync(stored, codes[0])).Succeeded);
        Assert.False((await later.Users.RedeemTwoFactorRecoveryCodeAsync(stored, codes[0])).Succeeded);
        Assert.Equal(2, await later.Users.CountRecoveryCodesAsync(stored));

        await later.Users.RemoveAuthenticationTokenAsync(stored, "github", "access_token");
        Assert.Null(await later.Users.GetAuthenticationTokenAsync(stored, "github", "access_token"));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Deleting_a_user_removes_everything_that_belonged_to_it()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        await scope.Roles.CreateAsync(new AppRole("Auditor"));
        var user = new AppUser { UserName = "ada", Email = "ada@example.org" };
        await scope.Users.CreateAsync(user);
        await scope.Users.AddToRoleAsync(user, "Auditor");
        await scope.Users.AddClaimAsync(user, new Claim("level", "3"));
        await scope.Users.AddLoginAsync(user, new UserLoginInfo("github", "42", "GitHub"));

        Assert.True((await scope.Users.DeleteAsync(user)).Succeeded);

        Assert.Null(await scope.Users.FindByIdAsync(user.Id));
        Assert.Null(await scope.Users.FindByNameAsync("ada"));
        Assert.Null(await scope.Users.FindByEmailAsync("ada@example.org"));
        Assert.Null(await scope.Users.FindByLoginAsync("github", "42"));
        Assert.Empty(await scope.Users.GetUsersInRoleAsync("Auditor"));
        Assert.Empty(await scope.Users.GetUsersForClaimAsync(new Claim("level", "3")));

        // The name is free again.
        Assert.True((await scope.Users.CreateAsync(new AppUser { UserName = "ada" })).Succeeded);
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Roles_are_created_renamed_deleted_and_have_claims()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var role = new AppRole("Auditor");

        Assert.True((await scope.Roles.CreateAsync(role)).Succeeded);
        Assert.False((await scope.Roles.CreateAsync(new AppRole("AUDITOR"))).Succeeded);
        Assert.True(await scope.Roles.RoleExistsAsync("auditor"));

        await scope.Roles.AddClaimAsync(role, new Claim("permission", "read"));
        Assert.Equal("read", Assert.Single(await scope.Roles.GetClaimsAsync(role)).Value);

        Assert.True((await scope.Roles.SetRoleNameAsync(role, "Reviewer")).Succeeded);
        Assert.True((await scope.Roles.UpdateAsync(role)).Succeeded);
        Assert.False(await scope.Roles.RoleExistsAsync("Auditor"));
        Assert.Equal(role.Id, (await scope.Roles.FindByNameAsync("Reviewer"))?.Id);

        Assert.True((await scope.Roles.DeleteAsync(role)).Succeeded);
        Assert.Empty(scope.Roles.Roles);
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task Role_that_was_deleted_is_no_longer_a_role_of_its_users()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        var role = new AppRole("Auditor");
        await scope.Roles.CreateAsync(role);
        var user = new AppUser { UserName = "ada" };
        await scope.Users.CreateAsync(user);
        await scope.Users.AddToRoleAsync(user, "Auditor");

        await scope.Roles.DeleteAsync(role);

        Assert.Empty(await scope.Users.GetRolesAsync(user));
    }

    [Fact(Skip = StoresNotImplemented)]
    public async Task All_users_can_be_listed()
    {
        await using var app = await IdentityApp.StartAsync();
        await using var scope = app.NewScope();
        foreach (var name in new[] { "ada", "grace", "linus" })
        {
            await scope.Users.CreateAsync(new AppUser { UserName = name });
        }

        Assert.Equal(["ada", "grace", "linus"], scope.Users.Users.Select(user => user.UserName).ToList().Order());
        Assert.Equal(1, scope.Users.Users.Count(user => user.NormalizedUserName == "GRACE"));
    }
}
