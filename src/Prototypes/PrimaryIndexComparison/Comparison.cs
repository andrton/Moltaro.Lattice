using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class Comparison
{
    public static ComparisonResult Run<TIndex, TKey>(IndexKindEnum kind, int count, int samples)
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
        {
            throw new InvalidOperationException("Use the runner or set DOTNET_TieredCompilation=0 before starting the process.");
        }

        WarmUp<TIndex, TKey>();
        var data = new KeyData<TKey>(count);
        var steadyState = SteadyState.Measure<TIndex, TKey>(data, samples);
        var growth = GrowthMeasurement.Measure<TIndex, TKey>(data);
        GC.KeepAlive(data);

        return new ComparisonResult(
            kind.ToString(), typeof(TKey).Name, count, samples, KeyData<TKey>.Seed,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            GCSettings.IsServerGC,
            VersionOf(typeof(Faster.Map.Core.BlitzMap<,>).Assembly),
            VersionOf(typeof(Arc.Collections.UnorderedMapSlim<,>).Assembly),
            sizeof(ulong), true, steadyState, growth);
    }

    private static void WarmUp<TIndex, TKey>()
        where TIndex : struct, IPrimaryIndex<TIndex, TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        var data = new KeyData<TKey>(4097);
        _ = SteadyState.Measure<TIndex, TKey>(data, 3);
        _ = GrowthMeasurement.Measure<TIndex, TKey>(data);
        GC.KeepAlive(data);
    }

    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException($"No version metadata: {assembly.GetName().Name}");
}
