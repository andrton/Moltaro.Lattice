namespace Moltaro.Lattice.Access.Updates;

internal enum UpdateCommandKindEnum : byte
{
    Assign = 1,
    ListAppend,
    ListInsert,
    ListReplace,
    ListRemove,
    MapSet,
    MapRemove,
    SetAdd,
    SetRemove,
}
