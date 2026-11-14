using Arc.Collections;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record PreparedIndexEntry(
    OrderedMultiMap<CompositeIndexKey, RootClaim>.Node Node,
    CompositeIndexKey Key,
    RootClaim Claim,
    bool Insertion);
