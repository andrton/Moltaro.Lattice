namespace Moltaro.Lattice.Engine.Writes;

// What the publication of a prepared write installs (WriteExecutor.Publish): a new root under a new
// root entry, a new root in the entry of a stored one, the delete of a stored one, or only a position,
// for a delete of a key that is absent.
internal enum PublicationKindEnum
{
    Insert = 1,

    Replace = 2,

    Delete = 3,

    NothingToDelete = 4,
}
