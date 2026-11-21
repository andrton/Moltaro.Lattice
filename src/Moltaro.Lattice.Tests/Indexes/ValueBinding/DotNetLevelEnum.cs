namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A .NET enum with the members and the underlying type of the enum type Level of BindingSchema: a value of
// it is still not a value of an Enum part, which is given as its integer (EnumAndFlagsTests).
internal enum DotNetLevelEnum : short
{
    Lowest = -300,
    Low = -2,
    High = 5,
    Highest = 300,
}
