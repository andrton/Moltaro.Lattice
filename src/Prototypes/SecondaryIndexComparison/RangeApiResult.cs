namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal sealed record RangeApiResult(int ExpectedCount, int? ElementsBetweenCount,
    string? ElementsBetweenError, int ElementsFromCount, bool ChosenPathVerified);
