using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// One call of a RootWriter, for WriterAssert. A writer is a ref struct, which a lambda cannot
// capture, so the call is given the writer by reference: what the call leaves in it stays there.
internal delegate void WriterAction(ref RootWriter writer);
