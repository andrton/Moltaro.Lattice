using System.Buffers;

namespace Moltaro.Lattice.Replication.Load;

// A key borrowed from a checked body, or a group of whole packed records from that body.
internal readonly record struct WireKey(ReadOnlySequence<byte> Bytes, bool IsNull);
