using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Moltaro.Lattice.Generators.Metadata;

// Only the compilation used for class agreement gets implementation references. The consumer's
// compiler still sees its original references. Reading a PE into Roslyn does not load its assembly.
internal sealed class ModelMetadata
{
    private const string MapSuffix = ".lattice-reference-map";

    private readonly Dictionary<AssemblyIdentity, string> _failures = [];

    public ModelMetadata(Compilation original, ImmutableArray<AdditionalText> files, CancellationToken cancellationToken)
    {
        var map = ReadMap(files, cancellationToken);
        var references = new List<MetadataReference>();
        foreach (var reference in original.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replacement = reference;
            if (reference is PortableExecutableReference { FilePath: { } path } portable &&
                original.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                map.TryGetValue(path, out var implementation))
            {
                replacement = ReadImplementation(portable, assembly.Identity, implementation);
            }

            references.Add(replacement);
        }

        Compilation = original.WithReferences(references).WithOptions(original.Options.WithMetadataImportOptions(MetadataImportOptions.All));
    }

    public Compilation Compilation { get; }

    public string? Unavailable(IAssemblySymbol assembly)
    {
        if (_failures.TryGetValue(assembly.Identity, out var failure))
        {
            return failure;
        }

        return assembly.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute")
            ? "only reference assembly metadata is available"
            : null;
    }

    private PortableExecutableReference ReadImplementation(PortableExecutableReference reference, AssemblyIdentity expected, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _failures[expected] = "the implementation file is missing";
            }
            else if (!AssemblyIdentity.TryParseDisplayName(AssemblyName.GetAssemblyName(path).FullName, out var identity) ||
                !expected.Equals(identity))
            {
                _failures[expected] = "the implementation assembly identity differs from the compiler reference";
            }
            else
            {
                return MetadataReference.CreateFromImage(File.ReadAllBytes(path), reference.Properties, filePath: path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            _failures[expected] = "the implementation file could not be read as a .NET assembly";
        }

        return reference;
    }

    private static Dictionary<string, string> ReadMap(ImmutableArray<AdditionalText> inputs, CancellationToken cancellationToken)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var map = new Dictionary<string, string>(pathComparer);
        var file = inputs.FirstOrDefault(input => input.Path.EndsWith(MapSuffix, StringComparison.Ordinal));
        var text = file?.GetText(cancellationToken);
        if (text is null)
        {
            return map;
        }

        foreach (var line in text.Lines)
        {
            var paths = line.ToString().Split('|', 2);
            if (paths.Length == 2)
            {
                map.TryAdd(paths[0], paths[1]);
            }
        }

        return map;
    }
}
