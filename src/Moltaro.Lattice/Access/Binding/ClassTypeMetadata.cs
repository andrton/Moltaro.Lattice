using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Binding;

// Reflection and compiler symbol readers supply type facts. ClassAgreement owns the rules
// that compare those facts to the compiled schema.
internal abstract class ClassTypeMetadata<TType>
    where TType : class
{
    public abstract string Name(TType type);

    public abstract string? FullName(TType type);

    public abstract bool Matches(TType type, Type expected);

    public abstract TType? ArrayElement(TType type);

    public abstract IReadOnlyList<TType>? GenericArguments(TType type, Type definition);

    public abstract bool IsEnum(TType type);

    public abstract List<string> EnumDifferences(TType type, LatticeEnumType declaration);
}
