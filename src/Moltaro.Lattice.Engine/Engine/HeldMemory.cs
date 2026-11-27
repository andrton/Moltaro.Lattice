using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine;

// What the budget of an instance charges and what its structures hold, taken under the lock of the
// writes (EngineDatabase.InspectHeld): the charge, its peak and the headroom; the native grants,
// the reserve included, and the bytes of each; and every array of the structures that grow with
// the data. A segment object belongs to its grant; its metadata arrays are among Arrays. Last, the
// physical capacity of the delivery tail, the arrays of its blocks, which is outside admission and
// not among Arrays.
internal readonly record struct HeldMemory(
    long Charged, long Peak, long Headroom, int GrantedSegments, int SegmentBytes, IReadOnlyList<HeldArray> Arrays,
    long DeliveryCapacityBytes);
