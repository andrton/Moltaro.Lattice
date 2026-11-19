namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// How the check under change makes entries and probes for one kind of entry, and reads an entry back
// to its key number and identity, which it compares against the model, outside the protection.
internal interface ICheckKeys<TEntry, TProbe>
    where TEntry : unmanaged
    where TProbe : unmanaged
{
    TEntry EntryOf(ulong id);

    // A probe at the key of the number with the identity.
    TProbe ProbeAt(int keyNumber, ulong id);

    int KeyNumberOf(in TEntry entry);

    ulong IdOf(in TEntry entry);
}
