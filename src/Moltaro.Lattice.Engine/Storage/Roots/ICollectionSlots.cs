namespace Moltaro.Lattice.Storage.Roots;

// A zeroed lookup table to fill. A capacity of zero selects the compact scan.
internal interface ICollectionSlots
{
    int Capacity { get; }

    ref ulong Slot(int index);
}
