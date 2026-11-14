namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal interface IRootTable<TAccess> : IDisposable where TAccess : struct, IRootAccess
{
    int Capacity { get; }
    int DirectoryCapacity { get; }
    long NativeBytes { get; }
    long ManagedStorageBytes { get; }
    void AddPage();
    TAccess GetAccess(int count);
}
