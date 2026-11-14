using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Moltaro.Lattice.DependencyInjection;

/// <summary>
/// Registers Lattice databases in a dependency injection container.
/// </summary>
/// <remarks>
/// <para>
/// An application can have any number of databases: one default database and any number
/// of named ones, masters and children alike. They are independent of each other.
/// </para>
/// <para>
/// Every registered database is one instance for the whole application. A background
/// service opens it when the application starts and closes it when the application stops;
/// the application does not finish starting, and a web application does not accept
/// requests, until the database is ready. Register databases before the background
/// services that use them, so that they are open when those start and still open while
/// those stop.
/// </para>
/// <para>
/// An application that builds a container without the .NET host opens its databases
/// itself with <see cref="ILatticeDatabase.OpenAsync"/>.
/// </para>
/// </remarks>
public static class LatticeServiceCollectionExtensions
{
    /// <summary>
    /// Adds the default database. Services get it as <see cref="ILatticeDatabase"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The configuration of the database instance.</param>
    /// <exception cref="InvalidOperationException">A default database is already registered.</exception>
    public static IServiceCollection AddLattice(this IServiceCollection services, LatticeDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddLattice(_ => options);
    }

    /// <summary>
    /// Adds the default database. Services get it as <see cref="ILatticeDatabase"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Creates the configuration of the database instance; it can read other services,
    /// such as the application configuration.
    /// </param>
    /// <exception cref="InvalidOperationException">A default database is already registered.</exception>
    public static IServiceCollection AddLattice(this IServiceCollection services, Func<IServiceProvider, LatticeDatabaseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(service => service.ServiceType == typeof(ILatticeDatabase) && !service.IsKeyedService))
        {
            throw new InvalidOperationException("A default Lattice database is already registered. Give the other databases names.");
        }

        services.AddSingleton(provider => LatticeDatabase.Create(configure(provider)));
        services.AddSingleton<IHostedService>(provider => new LatticeBackgroundService(provider.GetRequiredService<ILatticeDatabase>()));
        return services;
    }

    /// <summary>
    /// Adds a named database. Services get it as <see cref="ILatticeDatabase"/> by its
    /// name, as a keyed service: a constructor parameter marked
    /// <c>[FromKeyedServices("name")]</c>, or <c>GetRequiredKeyedService</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name of the database within the application.</param>
    /// <param name="options">The configuration of the database instance.</param>
    /// <exception cref="InvalidOperationException">A database with this name is already registered.</exception>
    public static IServiceCollection AddLattice(this IServiceCollection services, string name, LatticeDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services.AddLattice(name, _ => options);
    }

    /// <summary>
    /// Adds a named database. Services get it as <see cref="ILatticeDatabase"/> by its
    /// name, as a keyed service: a constructor parameter marked
    /// <c>[FromKeyedServices("name")]</c>, or <c>GetRequiredKeyedService</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name of the database within the application.</param>
    /// <param name="configure">
    /// Creates the configuration of the database instance; it can read other services,
    /// such as the application configuration.
    /// </param>
    /// <exception cref="InvalidOperationException">A database with this name is already registered.</exception>
    public static IServiceCollection AddLattice(this IServiceCollection services, string name, Func<IServiceProvider, LatticeDatabaseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(service => service.ServiceType == typeof(ILatticeDatabase) && service.IsKeyedService && Equals(service.ServiceKey, name)))
        {
            throw new InvalidOperationException($"A Lattice database named '{name}' is already registered.");
        }

        services.AddKeyedSingleton(name, (provider, _) => LatticeDatabase.Create(configure(provider)));
        services.AddSingleton<IHostedService>(provider => new LatticeBackgroundService(provider.GetRequiredKeyedService<ILatticeDatabase>(name)));
        return services;
    }
}
