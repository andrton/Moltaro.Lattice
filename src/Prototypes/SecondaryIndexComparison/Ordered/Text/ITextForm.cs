using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// One way an index entry holds a text key, with its order (the forms of the planner's question):
// - Root: form 1, the entry names the entity; a comparison reads the key from the root;
// - Copy: form 2, the entry holds a copy of the key;
// - PrefixRoot: form 2 bounded, the entry holds the first eight bytes, and the root breaks a tie;
// - Shared: form 3, the entry and the root name one shared text entry;
// - SharedPrefix: form 3 with the first eight bytes in the entry, the shared entry breaking a tie.
internal interface ITextForm<TEntry, TOrder>
    where TEntry : unmanaged
    where TOrder : struct, IEntryOrder<TEntry, TextProbe>
{
    static abstract string Name { get; }

    // Whether the form is a candidate for the engine. Root and PrefixRoot are not, and are measured as
    // reference lines only: a separator that names an entity goes on steering searches after the entity
    // is gone and its control entry is reused, and a replace that changes the key leaves the entry at
    // the old key's place while the root already holds the new one.
    static abstract bool IsCandidate { get; }

    // Whether the root keeps its text in an entry of its own, for the index to share.
    static abstract bool SharesText { get; }

    static abstract TEntry Make(ulong id, ReadOnlySpan<byte> text, ulong textAddress);

    static abstract TOrder OrderFor(TextWorld world);
}
