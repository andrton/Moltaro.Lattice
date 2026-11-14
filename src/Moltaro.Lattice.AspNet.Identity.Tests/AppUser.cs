namespace Moltaro.Lattice.AspNet.Identity.Tests;

// The application's user class, as an application using ASP.NET Core Identity declares it:
// the standard user with properties of its own. app.schema.json declares a field for each.
public sealed class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }

    public long? TenantId { get; set; }
}
