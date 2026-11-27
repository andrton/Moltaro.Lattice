namespace Moltaro.Lattice.Storage.Budgeting;

// A controlled refusal before publication. An allocator exception is a different failure.
internal sealed class BudgetRefusedException(LatticeLimitEnum limit) : Exception
{
    public LatticeLimitEnum Limit { get; } = limit;

    public LatticeException WriteRejected() => new(
        LatticeErrorEnum.WriteRejected,
        Limit == LatticeLimitEnum.RootSlots
            ? "The write needs another root slot, and none is available."
            : "The write needs more memory than the instance can hold.")
    {
        Limit = Limit,
    };
}
