namespace Moltaro.Lattice.Storage.Roots;

// Where the records of one List of kind Records come from, for the one placement of such a List
// (NestedValues.PlaceRecords), which makes each block of its records and leaves the records to the source: the
// elements of a List a write of values holds (ListRecords), or the blocks of records a node received in the
// load format (Replication.Load.ReceivedRecords). Implemented by a struct, a ref struct for what is received,
// and passed by reference, so that the placement is compiled for each source and calls it without virtual
// dispatch.
//
// The placement asks for the blocks in the order of the elements, and for each block first its length, then
// its records; a source that reads its records from an input reads them in that order.
internal interface IRecordSource
{
    // The payload of the block of count records from the element first on: count times the stride of the
    // element, and the bytes of every value of their descriptors that lies inline, after the records.
    int BlockLength(StructureLayout element, int first, int count);

    // Writes those records into the block of the holder, made with the length BlockLength gave, and places
    // through the holder what their descriptors name out of line; the holder says when the arena refused a
    // block (ValueHolder.Refused). The block of an arena comes as it was allocated, the block of a root for a
    // forwarded write zero.
    void Fill(ref ValueHolder records, StructureLayout element, int first, int count);
}
