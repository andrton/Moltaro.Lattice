namespace Moltaro.Lattice.Engine.Entities;

// What a prepared change does to its entity (PreparedChange): a new root under a new root entry, a new
// root in the entry of a stored entity, or the delete of a stored entity.
internal enum ChangeKindEnum
{
    Insert = 1,

    Replace = 2,

    Delete = 3,
}
