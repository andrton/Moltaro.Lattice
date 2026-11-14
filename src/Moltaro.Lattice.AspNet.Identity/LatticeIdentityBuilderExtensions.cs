using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Moltaro.Lattice.AspNet.Identity;

/// <summary>
/// Makes ASP.NET Core Identity keep its users and roles in a Lattice database.
/// </summary>
public static class LatticeIdentityBuilderExtensions
{
    /// <summary>
    /// Registers the Lattice stores for the user class, and for the role class when roles
    /// were added. Call it where an application would otherwise register the Entity
    /// Framework stores; <c>UserManager</c>, <c>SignInManager</c> and <c>RoleManager</c>
    /// work as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stores take <see cref="ILatticeDatabase"/> from the container: the default
    /// database, or the one registered under <paramref name="databaseName"/>.
    /// </para>
    /// <para>
    /// The schema of that database belongs to the application. It has to contain the entity
    /// types, fields and indexes described by <see cref="LatticeIdentitySchema"/>, which also
    /// provides a default declaration to start from. The schema is checked when a store is
    /// created, with the database open; a schema that lacks something is refused with
    /// <see cref="LatticeErrorEnum.InvalidSchema"/> and a list of what is missing.
    /// </para>
    /// <para>
    /// The user class derives from <see cref="IdentityUser{TKey}"/> and the role class from
    /// <see cref="IdentityRole{TKey}"/>, both with a <see cref="string"/> key. They may be the
    /// application's own classes with properties of their own.
    /// </para>
    /// <para>
    /// <b>Not implemented yet.</b> The stores are registered and can be resolved, but their
    /// operations throw <see cref="NotSupportedException"/>.
    /// </para>
    /// </remarks>
    /// <param name="builder">The Identity builder.</param>
    /// <param name="databaseName">
    /// The name the database was registered under; <see langword="null"/> for the default database.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The user or the role class does not have a <see cref="string"/> key.
    /// </exception>
    public static IdentityBuilder AddLatticeStores(this IdentityBuilder builder, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var userType = builder.UserType;
        var roleType = builder.RoleType ?? typeof(IdentityRole);
        RequireStringKey(userType, typeof(IdentityUser<string>));
        RequireStringKey(roleType, typeof(IdentityRole<string>));

        builder.Services.AddScoped(
            typeof(IUserStore<>).MakeGenericType(userType),
            provider => ActivatorUtilities.CreateInstance(
                provider,
                typeof(LatticeUserStore<,>).MakeGenericType(userType, roleType),
                ResolveDatabase(provider, databaseName, userType, roleType)));

        if (builder.RoleType is not null)
        {
            builder.Services.AddScoped(
                typeof(IRoleStore<>).MakeGenericType(roleType),
                provider => ActivatorUtilities.CreateInstance(
                    provider,
                    typeof(LatticeRoleStore<>).MakeGenericType(roleType),
                    ResolveDatabase(provider, databaseName, userType, roleType)));
        }

        return builder;
    }

    // The schema comes from the application, and on a child from its master, so it is
    // checked against the database a store is about to use, not at registration.
    private static ILatticeDatabase ResolveDatabase(IServiceProvider provider, string? databaseName, Type userType, Type roleType)
    {
        var database = databaseName is null
            ? provider.GetRequiredService<ILatticeDatabase>()
            : provider.GetRequiredKeyedService<ILatticeDatabase>(databaseName);

        LatticeIdentitySchema.Validate(database.Schema, userType, roleType);
        return database;
    }

    private static void RequireStringKey(Type type, Type requiredBase)
    {
        if (!requiredBase.IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"{type.Name} must derive from {requiredBase.Name[..^2]}<string>: the Lattice stores support string keys.");
        }
    }
}
