using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Updates;

// The private operands of one command and its declared field. The engine binds
// the path to its flattened layout before the command can be admitted.
internal readonly record struct UpdateCommand(
    UpdateCommandKindEnum Kind,
    string Name,
    LatticeField[] Path,
    int Position,
    object? First,
    object? Second,
    int Target = -1)
{
    public LatticeValueDeclaration Declaration => Path[^1].Declaration;

    public static LatticeValueDeclaration? FirstOf(UpdateCommandKindEnum kind, LatticeValueDeclaration field) => kind switch
    {
        UpdateCommandKindEnum.Assign => field,
        UpdateCommandKindEnum.MapSet or UpdateCommandKindEnum.MapRemove => field.Key,
        UpdateCommandKindEnum.ListRemove => null,
        _ => field.Element,
    };
}
