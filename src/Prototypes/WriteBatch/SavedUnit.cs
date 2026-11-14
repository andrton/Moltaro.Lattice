namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal readonly record struct SavedUnit(int Slot, int Part, byte[] Payload);
