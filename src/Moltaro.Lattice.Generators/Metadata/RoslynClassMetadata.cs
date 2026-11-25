using Microsoft.CodeAnalysis;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Generators.Metadata;

// Supplies the same facts as the reflection reader without loading a CLR model or
// invoking its accessors. Compiler symbols are from the implementation-metadata compilation.
internal sealed class RoslynClassMetadata(ModelMetadata model) : ClassTypeMetadata<ITypeSymbol>
{
    private readonly Compilation _compilation = model.Compilation;
    private readonly FrameworkMetadata _framework = new();

    public override string Name(ITypeSymbol type) => type.Name;

    public override string? FullName(ITypeSymbol type) => RuntimeName(type);

    public override bool Matches(ITypeSymbol type, Type expected)
    {
        if (expected.IsSZArray)
        {
            return ArrayElement(type) is { } element && Matches(element, expected.GetElementType()!);
        }

        if (expected.IsConstructedGenericType)
        {
            var actual = GenericArguments(type, expected.GetGenericTypeDefinition());
            var expectedArguments = expected.GetGenericArguments();
            return actual is not null && expectedArguments.Select((argument, index) => Matches(actual[index], argument)).All(matches => matches);
        }

        return SymbolEqualityComparer.Default.Equals(type, Definition(expected));
    }

    public override ITypeSymbol? ArrayElement(ITypeSymbol type) =>
        type is IArrayTypeSymbol { IsSZArray: true } array ? array.ElementType : null;

    public override IReadOnlyList<ITypeSymbol>? GenericArguments(ITypeSymbol type, Type definition) =>
        type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, Definition(definition))
            ? named.TypeArguments
            : null;

    public override bool IsEnum(ITypeSymbol type) => type.TypeKind == TypeKind.Enum;

    public override List<string> EnumDifferences(ITypeSymbol type, LatticeEnumType declaration)
    {
        var underlying = ((INamedTypeSymbol)type).EnumUnderlyingType!;
        var matches = Matches(underlying, FieldTypes.ValueType(declaration.UnderlyingType));
        var attribute = Definition(typeof(FlagsAttribute));
        var flags = type.GetAttributes().Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute));
        var members = type.GetMembers().OfType<IFieldSymbol>()
            .Where(field => field is { DeclaredAccessibility: Accessibility.Public, IsStatic: true, HasConstantValue: true })
            .Select(field => (field.Name, field.ConstantValue!));
        return DotNetEnumAgreement.Differences(declaration, matches, underlying.Name, flags, members);
    }

    public void CheckConstructor(INamedTypeSymbol type, string role) =>
        ClassAgreement.CheckConstructor(type.Name, role, type.IsAbstract, type.InstanceConstructors.Any(constructor =>
            constructor is { DeclaredAccessibility: Accessibility.Public, Parameters.Length: 0 }));

    public IPropertySymbol Property(INamedTypeSymbol type, string field, string owner) =>
        ClassAgreement.FindProperty(type, type.Name, field, owner, static current => current.BaseType, DeclaredMembers).Property;

    public IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            foreach (var member in declaring.GetMembers())
            {
                if (member.DeclaredAccessibility != Accessibility.Public || !names.Add(member.MetadataName))
                {
                    continue;
                }

                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false } property)
                {
                    continue;
                }

                var (getter, setter) = Accessors(property);
                if (getter is { DeclaredAccessibility: Accessibility.Public } &&
                    (setter is not null || model.Unavailable(property.ContainingAssembly) is not null))
                {
                    yield return property;
                }
            }
        }
    }

    public void CheckAccessors(IPropertySymbol property)
    {
        if (Accessors(property).Setter is null && model.Unavailable(property.ContainingAssembly) is { } failure)
        {
            throw MetadataFailure(property, failure);
        }
    }
    private ClassMember<IPropertySymbol, IMethodSymbol>[] DeclaredMembers(INamedTypeSymbol type, string field) =>
        [.. type.GetMembers().Where(member => member.MetadataName == field && member.DeclaredAccessibility == Accessibility.Public)
            .Select(Member)];

    private ClassMember<IPropertySymbol, IMethodSymbol> Member(ISymbol member)
    {
        if (member is not IPropertySymbol property)
        {
            var kind = member is INamedTypeSymbol ? "nested type" : member.Kind.ToString().ToLowerInvariant();
            return new(null, kind, false, false, false, null, null);
        }

        var (getter, setter) = Accessors(property);
        if (setter is null && model.Unavailable(property.ContainingAssembly) is { } failure)
        {
            throw MetadataFailure(property, failure);
        }

        return new(property, "property", property.IsIndexer, property.IsStatic,
            getter is { DeclaredAccessibility: Accessibility.Public }, getter, setter);
    }

    private static (IMethodSymbol? Getter, IMethodSymbol? Setter) Accessors(IPropertySymbol property)
    {
        var getter = property.GetMethod;
        var setter = property.SetMethod;
        for (var current = property.OverriddenProperty; current is not null && (getter is null || setter is null); current = current.OverriddenProperty)
        {
            getter ??= current.GetMethod;
            setter ??= current.SetMethod;
        }

        return (getter, setter);
    }

    private INamedTypeSymbol? Definition(Type type)
    {
        var definitions = _compilation.GetTypesByMetadataName(type.FullName!);
        var assemblyName = type.Assembly.GetName().Name;
        foreach (var candidate in definitions)
        {
            var identity = candidate.ContainingAssembly.Identity;
            if (identity.Name == assemblyName || (type.Assembly == typeof(object).Assembly && IsFramework(identity)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsFramework(AssemblyIdentity identity) =>
        identity.Name.StartsWith("System.", StringComparison.Ordinal) && !identity.PublicKeyToken.IsDefaultOrEmpty;

    // CLR full names separate nesting with '+' and qualify each closed generic argument
    // by assembly. Framework references can expose a type through a facade.
    private string? RuntimeName(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => RuntimeName(array.ElementType) + ArrayDimensions(array),
        INamedTypeSymbol named => ConstructedName(named),
        IDynamicTypeSymbol => RuntimeName(_compilation.GetSpecialType(SpecialType.System_Object)),
        _ => null,
    };

    private static string ArrayDimensions(IArrayTypeSymbol type)
    {
        if (type.IsSZArray)
        {
            return "[]";
        }

        return type.Rank == 1 ? "[*]" : "[" + new string(',', type.Rank - 1) + "]";
    }

    private string ConstructedName(INamedTypeSymbol type)
    {
        var name = MetadataName(type);
        var arguments = ContainingTypeArguments(type).ToArray();
        return arguments.Length == 0 ? name : name + "[" + string.Join(",", arguments.Select(argument => "[" + QualifiedArgument(argument) + "]")) + "]";
    }

    private static IEnumerable<ITypeSymbol> ContainingTypeArguments(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } containing)
        {
            foreach (var argument in ContainingTypeArguments(containing))
            {
                yield return argument;
            }
        }

        foreach (var argument in type.TypeArguments)
        {
            yield return argument;
        }
    }

    private string QualifiedArgument(ITypeSymbol type)
    {
        var name = RuntimeName(type);
        var element = type;
        while (element is IArrayTypeSymbol array)
        {
            element = array.ElementType;
        }

        if (element is IDynamicTypeSymbol)
        {
            element = _compilation.GetSpecialType(SpecialType.System_Object);
        }

        var identity = element is INamedTypeSymbol named ? _framework.DefiningAssembly(named) : null;
        return name + ", " + (identity ?? element.ContainingAssembly?.Identity.GetDisplayName());
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } containing)
        {
            return MetadataName(containing) + "+" + type.MetadataName;
        }

        return type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;
    }

    private static LatticeException MetadataFailure(IPropertySymbol property, string reason) =>
        new(LatticeErrorEnum.BindingFailed, $"Cannot certify field '{property.Name}' against '{property.ContainingAssembly.Name}': {reason}.");
}
