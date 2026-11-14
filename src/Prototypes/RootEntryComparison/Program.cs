using System.Text.Json;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args is not [var backend, var countText, var output]
                || !int.TryParse(countText, out var count)
                || count is not (32768 or TableGeometry.MaximumEntries))
            {
                throw new ArgumentException("Use <Managed|Native> <32768|262144> <output.json>.");
            }

            var result = backend switch
            {
                "Managed" => Comparison.Run<ManagedTable, ManagedAccess>(backend, count, static () => new ManagedTable()),
                "Native" => Comparison.Run<NativeTable, NativeAccess>(backend, count, static () => new NativeTable()),
                _ => throw new ArgumentException("Unknown table backend.")
            };
            File.WriteAllText(output, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
