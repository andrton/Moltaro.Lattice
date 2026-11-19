namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// What a reader searches a text index with: the text, its prefix and an identity (0 to start before
// every identity of the key). The text lies in memory the caller keeps, or in the arena when the probe
// was made from an entry inside the protection.
internal readonly unsafe struct TextProbe
{
    public TextProbe(byte* text, int length, ulong id)
    {
        Text = text;
        Length = length;
        Prefix = TextWorld.PrefixOf(new ReadOnlySpan<byte>(text, length));
        Id = id;
    }

    public byte* Text { get; }

    public int Length { get; }

    public ulong Prefix { get; }

    public ulong Id { get; }
}
