using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["ordered", ..])
        {
            return Ordered.OrderedProgram.Run(args.AsSpan(1));
        }

        try
        {
            object result;
            string path;
            if (args is ["sample", var map, var distributionText, var countText, var output])
            {
                var count = ParseCount(countText);
                var distribution = Enum.Parse<DistributionEnum>(distributionText);
                result = map switch
                {
                    "Arc" => Benchmark.Run<ArcIndex>(count, distribution),
                    "Kaos" => Benchmark.Run<KaosIndex>(count, distribution),
                    _ => throw new ArgumentException("Map must be Arc or Kaos.")
                };
                path = output;
            }
            else if (args is ["checks", var checkCountText, var checkOutput])
            {
                result = new
                {
                    RangeApi = ApiChecks.Range(),
                    Preparation = PreparationChecks.Run(ParseCount(checkCountText))
                };
                path = checkOutput;
            }
            else
            {
                throw new ArgumentException("Use sample <Arc|Kaos> <Unique|Repeated> <count> <output.json> or checks <count> <output.json>.");
            }

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json + Environment.NewLine);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int ParseCount(string text)
    {
        var count = int.Parse(text);
        Check.Require(count is >= 1024 and <= 20000, "Entry count must be between 1,024 and 20,000.");
        return count;
    }
}
