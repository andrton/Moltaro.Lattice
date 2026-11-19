using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered;

// The scenarios of the study of ordered structures for one changing thread and readers without a
// lock: `ordered <scenario> [--option value ...]`. README.md lists the scenarios, their options and the
// command lines.
internal static class OrderedProgram
{
    public static int Run(ReadOnlySpan<string> arguments)
    {
        if (arguments.IsEmpty)
        {
            Console.Error.WriteLine("Use: ordered <bytes|textbytes|check|find|range|writes|text|writecost|looks|primaryread> [--option value ...]; README.md lists the options.");

            return 1;
        }

        var options = Options.Parse(arguments[1..]);

        Line.Write(
            "run",
            ("scenario", arguments[0]),
            ("arguments", string.Join(' ', arguments[1..].ToArray())),
            ("processors", Environment.ProcessorCount),
            ("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Replace(' ', '_')),
            ("architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture),
#if DEBUG
            ("build", "Debug"));
#else
            ("build", "Release"));
#endif

        switch (arguments[0])
        {
            case "bytes":
                BytesScenario.Run(options);
                break;

            case "textbytes":
                TextBytesScenario.Run(options);
                break;

            case "check":
                CheckScenario.Run(options);
                break;

            case "find":
                ReadScenario.Run(options, range: false);
                break;

            case "range":
                ReadScenario.Run(options, range: true);
                break;

            case "writes":
                WritesScenario.Run(options);
                break;

            case "text":
                TextScenario.Run(options);
                break;

            case "writecost":
                WriteCostScenario.Run(options);
                break;

            case "looks":
                LooksScenario.Run(options);
                break;

            case "primaryread":
                PrimaryReadScenario.Run(options);
                break;

            default:
                Console.Error.WriteLine($"No scenario is named '{arguments[0]}'.");

                return 1;
        }

        return 0;
    }
}
