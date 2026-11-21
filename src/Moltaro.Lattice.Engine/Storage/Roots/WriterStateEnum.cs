namespace Moltaro.Lattice.Storage.Roots;

// Where a RootWriter stands in its life (brief of S05, "The life of a writer"). Only an open writer
// takes values; the three ends differ in what Abandon does after them.
internal enum WriterStateEnum : byte
{
    // Takes values; Complete or Abandon ends it.
    Open = 0,

    // Complete returned a root, which belongs to the caller: Abandon is refused.
    Completed = 1,

    // Complete failed and freed everything: Abandon does nothing.
    Failed = 2,

    // Abandon freed everything: a second Abandon does nothing.
    Abandoned = 3,
}
