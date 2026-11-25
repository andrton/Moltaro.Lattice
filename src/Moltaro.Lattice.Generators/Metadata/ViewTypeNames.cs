using Microsoft.CodeAnalysis;

namespace Moltaro.Lattice.Generators.Metadata;

// Render types in the same reference scope as the compiler. An alias-only
// reference has no global name, including when it supplies a generic argument.
internal sealed class ViewTypeNames
{
    private readonly Dictionary<IAssemblySymbol, string?> _aliases = new(SymbolEqualityComparer.Default);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public ViewTypeNames(Compilation compilation)
    {
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            {
                continue;
            }

            var aliases = reference.Properties.Aliases;
            if (aliases.IsDefaultOrEmpty || aliases.Contains("global", StringComparer.Ordinal))
            {
                _aliases[assembly] = null;
            }
            else
            {
                _aliases.TryAdd(assembly, aliases[0]);
            }
        }
    }

    public IEnumerable<string> UsedAliases => _used.Order(StringComparer.Ordinal);

    public string Format(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return $"{Format(array.ElementType)}[{new string(',', array.Rank - 1)}]";
        }

        if (type is not INamedTypeSymbol named)
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return Format(named.TypeArguments[0]) + "?";
        }

        var prefix = named.ContainingType is { } enclosing
            ? Format(enclosing) + "."
            : RootQualifier(named) + NamespacePrefix(named.ContainingNamespace);
        var result = prefix + "@" + named.Name;
        if (named.Arity != 0)
        {
            result += "<" + string.Join(", ", named.TypeArguments.Select(Format)) + ">";
        }

        return result;
    }

    private string RootQualifier(INamedTypeSymbol type)
    {
        if (!_aliases.TryGetValue(type.ContainingAssembly, out var alias) || alias is null)
        {
            return "global::";
        }

        _used.Add(alias);
        return "@" + alias + "::";
    }

    private static string NamespacePrefix(INamespaceSymbol space) =>
        space.IsGlobalNamespace ? "" : NamespacePrefix(space.ContainingNamespace) + "@" + space.Name + ".";
}
