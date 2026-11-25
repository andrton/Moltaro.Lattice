using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Binding;

internal sealed class ReflectionClassMetadata : ClassTypeMetadata<Type>
{
    public static ReflectionClassMetadata Instance { get; } = new();

    public override string Name(Type type) => type.Name;

    public override string? FullName(Type type) => type.FullName;

    public override bool Matches(Type type, Type expected) => type == expected;

    public override Type? ArrayElement(Type type) => type.IsSZArray ? type.GetElementType() : null;

    public override IReadOnlyList<Type>? GenericArguments(Type type, Type definition) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == definition ? type.GetGenericArguments() : null;

    public override bool IsEnum(Type type) => type.IsEnum;

    public override List<string> EnumDifferences(Type type, LatticeEnumType declaration) =>
        DotNetEnumAgreement.Differences(type, declaration);
}
