using System.Runtime.CompilerServices;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Views;

// Resolves the paths of a generated projection to the exact field handles for an entity type.
// The cache shares this immutable array wherever the same compiled schema is used. The weak
// key lets the handles and their declarations go with the schema. The projection is bound
// before entering read protection, including when the key being looked up is absent.
internal static class ViewBinding<TView, TEntity>
    where TView : ILatticeEntityView<TView, TEntity>, allows ref struct
    where TEntity : class
{
    private static readonly ConditionalWeakTable<LatticeEntityType, LatticeField[]> Bindings = [];

    public static LatticeField[] For(LatticeEntityType type) => Bindings.GetValue(type, Bind);

    private static LatticeField[] Bind(LatticeEntityType type)
    {
        var unbound = new Dictionary<string, LatticeField>(StringComparer.Ordinal);
        AddFields(unbound, "", type.Fields);

        var projection = TView.Fields;
        var handles = new LatticeField[projection.Length];
        for (var index = 0; index < projection.Length; index++)
        {
            var expected = projection[index];
            if (!unbound.Remove(expected.Path, out var field))
            {
                throw Failure(type, expected.Path, "the schema has no matching field or the path is repeated in the view");
            }

            var actual = field.Declaration;
            if (expected.RequiredKind is { } kind && kind != actual.Type)
            {
                throw Failure(type, expected.Path, $"the view requires {kind} and the schema declares {actual.Type}");
            }

            if (expected.RequiredNullable is { } nullable && nullable != actual.IsNullable)
            {
                var expectedForm = nullable ? "nullable" : "required";
                var actualForm = actual.IsNullable ? "nullable" : "required";
                throw Failure(type, expected.Path, $"the view requires a {expectedForm} field and the schema declares it {actualForm}");
            }

            handles[index] = field;
        }

        if (unbound.Count != 0)
        {
            throw Failure(type, unbound.Keys.First(), "the schema declares a field that the view does not project");
        }

        return handles;
    }

    private static void AddFields(Dictionary<string, LatticeField> paths, string prefix, IReadOnlyList<LatticeField> fields)
    {
        foreach (var field in fields)
        {
            var path = prefix + field.Name;
            paths.Add(path, field);
            if (field.Declaration.OwnedType is { } owned)
            {
                AddFields(paths, path + ".", owned.Fields);
            }
        }
    }

    private static LatticeException Failure(LatticeEntityType type, string path, string reason) =>
        new(LatticeErrorEnum.BindingFailed, $"Field '{path}' of '{type.Name}' cannot bind to view '{typeof(TView).Name}': {reason}.");
}
