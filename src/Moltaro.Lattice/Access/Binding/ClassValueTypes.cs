namespace Moltaro.Lattice.Access.Binding;

// The CLR types of a declaration's parts, when its form needs more than a type check.
internal readonly record struct ClassValueTypes<TType>(
    TType? Element = null, TType? Key = null, TType? Value = null, TType? Enum = null, bool IsArray = false)
    where TType : class;
