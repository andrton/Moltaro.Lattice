namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal readonly record struct EntityKey(EntityTypeEnum Type, int Key)
{
    // A fixture-only collision-free encoding for two types and Int32 keys 0..511.
    // Both types use the existing Arc primary index; this is not a public key format.
    public int Slot => (int)Type * 512 + Key;
    public static EntityKey FromSlot(int slot) => new((EntityTypeEnum)(slot / 512), slot % 512);
}
