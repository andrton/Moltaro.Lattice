namespace Moltaro.Lattice.Storage.Roots;

// What one walk over the descriptors of a root does with each of them (StructureWalk): the free of a root,
// its publication, its retirement with the bytes of its text entries, the bytes of its slots, the bytes an
// entity holds for a read (RootReader.EntityBytes), and the audit of the ownership of blocks by the tests. The
// export of a root to a child is not among them: the wire carries a root in preorder, and the walk visits
// children first (Replication.Load.BlockExport). Implemented by a struct and passed by reference, so that the walk is
// compiled for each visitor and calls it without virtual dispatch, as a read calls its copy (Engine.Entities
// .IRootCopy).
//
// The walk calls Visit for every descriptor of the root and of the records of its Lists, at every depth, and
// for each one after everything the value it names holds, children first: the descriptors of the records of
// a List of kind Records are visited before the descriptor of that List, so that a free never reads a block
// it freed. A descriptor of kind Null names nothing, and one of kind Inline lies in the block that holds it;
// the visitor decides what each kind means to it.
internal interface IStructureVisitor
{
    // One descriptor, as it lies at the offset given, counted from the start of the block given, its header
    // included: the root block for a descriptor of the root, the block of records that holds the record for a
    // descriptor of a record. That block is the parent of every block the descriptor names directly, and the
    // offset is the DescriptorOffset in their headers. The element is the layout of the records of a List of
    // kind Records, which the walk has visited; null for any other descriptor.
    void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element);
}
