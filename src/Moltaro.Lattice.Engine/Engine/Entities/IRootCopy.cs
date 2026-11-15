using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// How a read makes the owned copy of one root that it returns: an object of a class or a record
// (Q06: the read protects access while the engine fills a .NET result). A value type, so that the
// read protocol of EntityTable is compiled for each copy and calls it without virtual dispatch.
//
// Create makes the empty copy before the read takes the protection of the root, so that the
// constructor of a class, which is the application's code, runs under no gate. Fill copies every
// value of the root into it under that protection, from the reader, which is valid only then.
internal interface IRootCopy<out TResult>
    where TResult : class
{
    // The copy, after Create.
    TResult? Result { get; }

    void Create();

    void Fill(in RootReader reader);
}
