using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Writes;

// The inputs carried across admission, including the optional wait for a session.
// A batch supplies members and keys in place of the inputs of a single write.
internal readonly record struct WriteSubmission(
    EntityTable? Table = null,
    WriteKindEnum Kind = default,
    EntityKey Key = default,
    object?[]? Values = null,
    LatticeRevision? ExpectedRevision = null,
    BatchMember[]? Members = null,
    PublicationKeys? Keys = null,
    ForwardedOrigin? Forwarded = null,
    UpdateInput? Update = null);
