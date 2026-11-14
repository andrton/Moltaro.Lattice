using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["integration"])
            {
                IntegrationChecks.Run();
                return 0;
            }

            if (args is ["platform"])
            {
                IntegrationChecks.CheckPlatform();
                return 0;
            }

            if (args is ["verify"])
            {
                Verification.RunAll();
                Console.WriteLine("All four maps passed Int64, Guid and String verification.");
                return 0;
            }

            if (args.Length != 6 || args[0] != "sample")
            {
                throw new ArgumentException(
                    "Usage: verify | integration | platform | sample <Blitz|Dense|ArcSlim|Arc> <Int64|Guid|String> <count> <samples> <result.json>");
            }

            var kind = Enum.Parse<IndexKindEnum>(args[1], ignoreCase: true);
            var keyKind = Enum.Parse<KeyKindEnum>(args[2], ignoreCase: true);
            var count = int.Parse(args[3]);
            var samples = int.Parse(args[4]);
            if (count is < 1024 or > 2_000_000 || samples is < 3 or > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "Count: 1,024..2,000,000; samples: 3..15.");
            }

            var report = keyKind switch
            {
                KeyKindEnum.Int64 => Run<long>(kind, count, samples),
                KeyKindEnum.Guid => Run<Guid>(kind, count, samples),
                KeyKindEnum.String => Run<string>(kind, count, samples),
                _ => throw new ArgumentOutOfRangeException(nameof(keyKind))
            };

            File.WriteAllText(args[5], JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Console.WriteLine($"{kind} / {keyKind} / {count:N0}: verified; result saved.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static ComparisonResult Run<TKey>(IndexKindEnum kind, int count, int samples)
        where TKey : notnull, IEquatable<TKey>
    {
        return kind switch
        {
            IndexKindEnum.Blitz => Comparison.Run<BlitzIndex<TKey>, TKey>(kind, count, samples),
            IndexKindEnum.Dense => Comparison.Run<DenseIndex<TKey>, TKey>(kind, count, samples),
            IndexKindEnum.ArcSlim => Comparison.Run<ArcSlimIndex<TKey>, TKey>(kind, count, samples),
            IndexKindEnum.Arc => Comparison.Run<ArcIndex<TKey>, TKey>(kind, count, samples),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
