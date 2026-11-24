namespace Moltaro.Lattice.Storage.Roots;

// Keys by entry ordinal, for the same uniqueness check over placed and received entries.
internal interface ICollectionKeySource
{
    uint Hash(int ordinal);

    bool Equal(int left, int right);
}
