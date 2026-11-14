using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Moltaro.Lattice.AspNet.Identity;

// The user store. The base class from Microsoft.Extensions.Identity.Stores implements every
// Identity store interface on top of the members below, which are the only ones that
// touch storage: passwords, lockout, stamps, the authenticator key and recovery codes all
// work through them.
//
// Not implemented yet. Each member names what it will do in the database. Uniqueness is
// left to the unique indexes of the schema: a store never reads first to see whether a
// value is free. A change of several entities is one write batch.
internal sealed class LatticeUserStore<TUser, TRole>
    : UserStoreBase<TUser, TRole, string, IdentityUserClaim<string>, IdentityUserRole<string>, IdentityUserLogin<string>, IdentityUserToken<string>, IdentityRoleClaim<string>>
    where TUser : IdentityUser<string>
    where TRole : IdentityRole<string>
{
    public LatticeUserStore(ILatticeDatabase database, IdentityErrorDescriber describer)
        : base(describer)
    {
        Database = database;
    }

    public ILatticeDatabase Database { get; }

    // Every user. Backs administrative lists.
    public override IQueryable<TUser> Users => throw NotImplemented();

    // --- The user itself ---

    // Insert the user. The unique index on the normalized user name refuses a taken name,
    // which is reported as a duplicate user name.
    public override Task<IdentityResult> CreateAsync(TUser user, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Replace the user if it still has the revision it was read with. A lost race is a
    // concurrency failure; a name taken by another user is a duplicate user name.
    public override Task<IdentityResult> UpdateAsync(TUser user, CancellationToken cancellationToken = default) => throw NotImplemented();

    // One batch: the user and its roles, claims, logins and tokens, found through their
    // indexes by user. A user with more of them than a batch may hold is not deleted in parts.
    public override Task<IdentityResult> DeleteAsync(TUser user, CancellationToken cancellationToken = default) => throw NotImplemented();

    // By primary key.
    public override Task<TUser?> FindByIdAsync(string userId, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the unique index on the normalized user name.
    public override Task<TUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the index on the normalized email.
    public override Task<TUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) => throw NotImplemented();

    protected override Task<TUser?> FindUserAsync(string userId, CancellationToken cancellationToken) => throw NotImplemented();

    // --- Roles of a user ---

    // Insert the user role. The unique index on user and role refuses a second membership.
    public override Task AddToRoleAsync(TUser user, string normalizedRoleName, CancellationToken cancellationToken = default) => throw NotImplemented();

    public override Task RemoveFromRoleAsync(TUser user, string normalizedRoleName, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the index on user and role, by the user alone; then each role by primary key.
    public override Task<IList<string>> GetRolesAsync(TUser user, CancellationToken cancellationToken = default) => throw NotImplemented();

    public override Task<bool> IsInRoleAsync(TUser user, string normalizedRoleName, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the index of user roles by role; then each user by primary key.
    public override Task<IList<TUser>> GetUsersInRoleAsync(string normalizedRoleName, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the unique index on the normalized role name.
    protected override Task<TRole?> FindRoleAsync(string normalizedRoleName, CancellationToken cancellationToken) => throw NotImplemented();

    // Through the unique index on user and role.
    protected override Task<IdentityUserRole<string>?> FindUserRoleAsync(string userId, string roleId, CancellationToken cancellationToken) => throw NotImplemented();

    // --- Claims of a user ---

    // Through the index of claims by user.
    public override Task<IList<Claim>> GetClaimsAsync(TUser user, CancellationToken cancellationToken = default) => throw NotImplemented();

    // One batch for all the claims given.
    public override Task AddClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken = default) => throw NotImplemented();

    public override Task ReplaceClaimAsync(TUser user, Claim claim, Claim newClaim, CancellationToken cancellationToken = default) => throw NotImplemented();

    public override Task RemoveClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the index of claims by type and value; then each user by primary key.
    public override Task<IList<TUser>> GetUsersForClaimAsync(Claim claim, CancellationToken cancellationToken = default) => throw NotImplemented();

    // --- External logins of a user ---

    // Insert the login. The unique index on provider and key refuses an external account
    // that already belongs to a user.
    public override Task AddLoginAsync(TUser user, UserLoginInfo login, CancellationToken cancellationToken = default) => throw NotImplemented();

    public override Task RemoveLoginAsync(TUser user, string loginProvider, string providerKey, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the index of logins by user.
    public override Task<IList<UserLoginInfo>> GetLoginsAsync(TUser user, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the unique index on provider and key. This is the sign-in through an external provider.
    protected override Task<IdentityUserLogin<string>?> FindUserLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken) => throw NotImplemented();

    protected override Task<IdentityUserLogin<string>?> FindUserLoginAsync(string userId, string loginProvider, string providerKey, CancellationToken cancellationToken) => throw NotImplemented();

    // --- Named values of a user ---

    // Through the unique index on user, provider and name.
    protected override Task<IdentityUserToken<string>?> FindTokenAsync(TUser user, string loginProvider, string name, CancellationToken cancellationToken) => throw NotImplemented();

    protected override Task AddUserTokenAsync(IdentityUserToken<string> token) => throw NotImplemented();

    protected override Task RemoveUserTokenAsync(IdentityUserToken<string> token) => throw NotImplemented();

    private static NotSupportedException NotImplemented() =>
        new("The Lattice user store is not implemented yet.");
}
