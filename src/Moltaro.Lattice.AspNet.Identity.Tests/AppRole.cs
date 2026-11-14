namespace Moltaro.Lattice.AspNet.Identity.Tests;

// The application's role class.
public sealed class AppRole : IdentityRole
{
    public AppRole()
    {
    }

    public AppRole(string roleName)
        : base(roleName)
    {
    }
}
