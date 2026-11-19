namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// Form 3 with a prefix: the first eight bytes of the key, the address of the shared text entry and
// the identity, 24 bytes, after the 16-byte strings of DuckDB (string_t of
// src/include/duckdb/common/types/string_type.hpp, MIT, commit aa87a115e8ad8affc3a4ee2e373525cbb2c6efd8:
// length, a 4-byte prefix, a pointer) and the binary view of the Arrow columnar format (length, a 4-byte
// prefix, a buffer and an offset; Apache-2.0).
// A comparison reads the shared text only when the prefixes are equal.
internal readonly struct SharedPrefixEntry : ITextForm<SharedPrefixEntry, SharedPrefixOrder>
{
    public SharedPrefixEntry(ulong prefix, ulong text, ulong id)
    {
        Prefix = prefix;
        Text = text;
        Id = id;
    }

    public static string Name => "SharedPrefix";

    public static bool IsCandidate => true;

    public static bool SharesText => true;

    public ulong Prefix { get; }

    public ulong Text { get; }

    public ulong Id { get; }

    public static SharedPrefixEntry Make(ulong id, ReadOnlySpan<byte> text, ulong textAddress) => new(TextWorld.PrefixOf(text), textAddress, id);

    public static SharedPrefixOrder OrderFor(TextWorld world) => new(world);
}
