using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// How a read makes the owned copy of one root that it returns: an object of a class or a record (Q06:
// the read protects access while it copies). A value type, so that the read protocol of EntityTable
// is compiled for each copy and calls it without virtual dispatch.
//
// The engine runs no code of the application inside the read gate (the planner's word on the first
// review of S06). So a read has two steps. Copy runs inside the gate: it copies the values of the
// root, from the reader, which is valid only then, into memory of the engine and runs no code of
// the application. Build runs after the read has left the gate, and only when there is something to
// return: it makes the result from the copy, and the constructor and the set accessors of a class
// run there. Return gives back whatever Copy rented, and the read calls it in a finally,
// so that a constructor or a set accessor that throws leaves nothing rented; a second Return does
// nothing.
internal interface IRootCopy<out TResult>
    where TResult : class
{
    void Copy(in RootReader reader);

    TResult Build();

    void Return();
}
