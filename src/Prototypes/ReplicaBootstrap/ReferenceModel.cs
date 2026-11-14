namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

// Independent logical oracle: no binary codec, native state, replay rules or capture metadata.
internal sealed class ReferenceModel
{
    private readonly Dictionary<RootKey, DocumentValue> _values = [];

    public IReadOnlyDictionary<RootKey, DocumentValue> Values => _values;

    public void Apply(Mutation[] mutations)
    {
        foreach (var mutation in mutations)
        {
            switch (mutation.Kind)
            {
                case MutationKindEnum.Upsert:
                    _values[mutation.Key] = new DocumentValue(mutation.Number, mutation.Items.ToArray());
                    break;
                case MutationKindEnum.SetNumber:
                    _values[mutation.Key] = _values[mutation.Key] with { Number = mutation.Number };
                    break;
                case MutationKindEnum.Append:
                    var previous = _values[mutation.Key];
                    _values[mutation.Key] = previous with { Items = [.. previous.Items, mutation.Number] };
                    break;
                case MutationKindEnum.Delete:
                    Check.That(_values.Remove(mutation.Key), "Oracle deletion requires an existing fixture root.");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutations));
            }
        }
    }
}
