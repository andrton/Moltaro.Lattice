namespace Moltaro.Lattice.Access.Binding;

// The facts about a public member that decide whether it can supply a class property.
internal readonly record struct ClassMember<TProperty, TAccessor>(
    TProperty? Property, string Kind, bool IsIndexer, bool IsStatic, bool HasPublicGet, TAccessor? Getter, TAccessor? Setter)
    where TProperty : class
    where TAccessor : class;
