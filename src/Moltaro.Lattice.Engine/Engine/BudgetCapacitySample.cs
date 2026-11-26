namespace Moltaro.Lattice.Engine;

// Test and measurement evidence for the current budget, its high-water mark and delivery capacity.
internal readonly record struct BudgetCapacitySample(long ChargedBytes, long PeakBytes, long NativeBytes, long DeliveryCapacityBytes);
