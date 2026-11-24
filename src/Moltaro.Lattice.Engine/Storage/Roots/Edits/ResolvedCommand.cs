using Moltaro.Lattice.Access.Updates;

namespace Moltaro.Lattice.Storage.Roots.Edits;

// One effective command. An append has become an insert at its actual position.
// Map replacement requires a resident key and carries its stored representative.
internal readonly record struct ResolvedCommand(UpdateCommand Command, bool ReplacesMapValue = false);
