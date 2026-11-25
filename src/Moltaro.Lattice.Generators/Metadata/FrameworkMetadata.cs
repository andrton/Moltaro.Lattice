using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace Moltaro.Lattice.Generators.Metadata;

// The SDK's reference assembly may declare a framework type that the shared
// runtime forwards. CLR FullName uses the defining assembly. Follow only exact
// identities in the compiler host's shared runtime directory, reading PE metadata.
internal sealed class FrameworkMetadata
{
    private readonly string _directory = RuntimeEnvironment.GetRuntimeDirectory();
    private readonly Dictionary<INamedTypeSymbol, string?> _assemblies = new(SymbolEqualityComparer.Default);

    public string? DefiningAssembly(INamedTypeSymbol type)
    {
        var root = type.OriginalDefinition;
        while (root.ContainingType is { } enclosing)
        {
            root = enclosing.OriginalDefinition;
        }

        if (!_assemblies.TryGetValue(root, out var identity))
        {
            identity = FindAssembly(root);
            _assemblies.Add(root, identity);
        }

        return identity;
    }

    private string? FindAssembly(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        var space = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        var visited = new HashSet<AssemblyIdentity>();
        var current = type.ContainingAssembly.Identity;
        while (!current.PublicKeyToken.IsDefaultOrEmpty && Path.GetFileName(current.Name) == current.Name && visited.Add(current))
        {
            var path = Path.Combine(_directory, current.Name + ".dll");
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var runtime = metadata.GetAssemblyDefinition().GetAssemblyName().FullName;
            if (!AssemblyIdentity.TryParseDisplayName(runtime, out var definition) || !current.Equals(definition))
            {
                return null;
            }

            if (Defines(metadata, space, name))
            {
                return runtime;
            }

            if (ForwardedTo(metadata, space, name) is not { } assembly)
            {
                return null;
            }

            current = assembly;
        }

        return null;
    }

    private static bool Defines(MetadataReader metadata, string space, string name)
    {
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (metadata.StringComparer.Equals(type.Name, name) && metadata.StringComparer.Equals(type.Namespace, space) &&
                type.GetDeclaringType().IsNil)
            {
                return true;
            }
        }

        return false;
    }

    private static AssemblyIdentity? ForwardedTo(MetadataReader metadata, string space, string name)
    {
        foreach (var handle in metadata.ExportedTypes)
        {
            var type = metadata.GetExportedType(handle);
            if (type.IsForwarder && type.Implementation.Kind == HandleKind.AssemblyReference &&
                metadata.StringComparer.Equals(type.Name, name) && metadata.StringComparer.Equals(type.Namespace, space))
            {
                var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.Implementation).GetAssemblyName().FullName;
                return AssemblyIdentity.TryParseDisplayName(reference, out var identity) ? identity : null;
            }
        }

        return null;
    }
}
