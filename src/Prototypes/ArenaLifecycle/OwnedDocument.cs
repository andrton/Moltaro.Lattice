namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record OwnedDocument(ReadSummary Summary, byte[] Payload, string Text);
