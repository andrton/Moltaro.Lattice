using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Moltaro.Lattice.AspNet.Identity;

// The role store, on the base class from Microsoft.Extensions.Identity.Stores.
//
// Not implemented yet. Each member names what it will do in the database. Uniqueness of
// role names is left to the unique index of the schema.
internal sealed class LatticeRoleStore<TRole>
    : RoleStoreBase<TRole, string, IdentityUserRole<string>, IdentityRoleClaim<string>>
    where TRole : IdentityRole<string>
{
    public LatticeRoleStore(ILatticeDatabase database, IdentityErrorDescriber describer)
        : base(describer)
    {
        Database = database;
    }

    public ILatticeDatabase Database { get; }

    // Every role.
    public override IQueryable<TRole> Roles => throw NotImplemented();

    // Insert the role. The unique index on the normalized name refuses a taken name.
    public override Task<IdentityResult> CreateAsync(TRole role, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Replace the role if it still has the revision it was read with. A lost race is a
    // concurrency failure; a name taken by another role is a duplicate role name.
    public override Task<IdentityResult> UpdateAsync(TRole role, CancellationToken cancellationToken = default) => throw NotImplemented();

    // One batch: the role and its claims. Roles of users that point to it are left; a
    // missing role is skipped when they are read.
    public override Task<IdentityResult> DeleteAsync(TRole role, CancellationToken cancellationToken = default) => throw NotImplemented();

    // By primary key.
    public override Task<TRole?> FindByIdAsync(string id, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the unique index on the normalized name.
    public override Task<TRole?> FindByNameAsync(string normalizedName, CancellationToken cancellationToken = default) => throw NotImplemented();

    // Through the index of role claims by role.
    public override Task<IList<Claim>> GetClaimsAsync(TRole role, CancellationToken cancellationToken = default) => throw NotImplemented();

    public override Task AddClaimAsync(TRole role, Claim claim, CancellationToken cancellationToken = default) => throw NotImplemented();

    public override Task RemoveClaimAsync(TRole role, Claim claim, CancellationToken cancellationToken = default) => throw NotImplemented();

    private static NotSupportedException NotImplemented() =>
        new("The Lattice role store is not implemented yet.");
}
