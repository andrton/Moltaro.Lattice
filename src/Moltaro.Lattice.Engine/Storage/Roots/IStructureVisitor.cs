namespace Moltaro.Lattice.Storage.Roots;

// The callbacks of StructureWalk. EnterRecordBlock runs before the block's descriptors, so export
// can write the payload before the values it names. Visit runs for each descriptor of the root or
// of a record, after visiting the descriptors of the record blocks it names. This lets a visitor
// free a parent only after it has seen everything the parent owns.
//
// Visitors are structs passed by reference. The walk specializes for each visitor and calls its
// members without boxing or virtual dispatch. The caller holds the root's protection for the walk.
// See docs/design/arena-protocol.md, "Stored structures and root transfer."
internal interface IStructureVisitor
{
    // Whether to enter a descriptor and what it names. Lifecycle visitors
    // skip an older alias before reading the borrowed block's descendants.
    bool EnterValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset);

    // The payload of a block of records, before any descriptor of its records is visited.
    void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count);

    // One descriptor, as it lies at the offset given, counted from the start of the block given, its header
    // included: the root block for a descriptor of the root, the block of records that holds the record for a
    // descriptor of a record. That block is the parent of every block the descriptor names directly, and the
    // offset is the DescriptorOffset in their headers. The element is the layout of the records of a List of
    // kind Records, which the walk has visited; null for any other descriptor.
    void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element);
}
