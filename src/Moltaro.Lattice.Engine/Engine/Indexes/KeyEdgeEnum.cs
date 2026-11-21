namespace Moltaro.Lattice.Engine.Indexes;

// Where a search key that names no identity stands among the entries whose parts equal the parts it
// gives (SearchKey): before every one of them or after every one of them. So a walk starts and ends at an
// inclusive or an exclusive bound of a range, and at the leading parts of a key alone, in either
// direction: an inclusive lower bound starts before every entry of its value, an exclusive one after
// every entry of it.
internal enum KeyEdgeEnum
{
    BeforeEvery,

    AfterEvery,
}
