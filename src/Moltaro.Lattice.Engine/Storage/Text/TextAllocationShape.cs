namespace Moltaro.Lattice.Storage.Text;

// An allocation group measured from resident managed objects under Writes.
internal readonly record struct TextAllocationShape(int InstanceCount, int ObjectFieldBytes, int ArrayElementBytes, int ArrayLength);
