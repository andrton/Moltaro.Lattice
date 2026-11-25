using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Entities;

// How a read makes the owned copy of one root that it returns: an object of a class or a record (Q06:
// the read protects access while it copies). A value type, so that the read protocol of EntityTable
// is compiled for each copy and calls it without virtual dispatch.
//
// An owned read has two steps (docs/design/arena-protocol.md, "Deferred reclamation").
// Copy runs inside the epoch. It copies the root into engine memory without waiting or running
// application code. The reader is valid only during that step.
// Build runs after the read has left the protection, and only when there is something to return:
// it makes the result from the copy, and the constructor and the set accessors of a class run there.
// Return gives back whatever Copy rented, and the read calls it in a finally, so that a constructor
// or a set accessor that throws leaves nothing rented; a second Return does nothing.
internal interface IRootCopy<out TResult>
    where TResult : class
{
    void Copy(in RootReader reader);

    TResult Build();

    void Return();
}
