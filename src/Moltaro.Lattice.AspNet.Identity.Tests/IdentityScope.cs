namespace Moltaro.Lattice.AspNet.Identity.Tests;

// The Identity managers of one unit of work.
internal sealed class IdentityScope(AsyncServiceScope scope) : IAsyncDisposable
{
    public UserManager<AppUser> Users => scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

    public RoleManager<AppRole> Roles => scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
