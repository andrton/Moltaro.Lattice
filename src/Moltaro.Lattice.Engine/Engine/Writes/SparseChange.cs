using Moltaro.Lattice.Storage.Roots.Edits;

namespace Moltaro.Lattice.Engine.Writes;

// The effective commands applied to one member's published predecessor.
internal readonly record struct SparseChange(ulong BaseSequence, ResolvedCommand[] Commands, long CommandBytes);
