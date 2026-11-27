namespace Moltaro.Lattice.Access.Updates;

// A submitted update owns normalized, schema-valid operands. Neither they nor the
// command order can be changed by the builder after it hands them over.
internal sealed class UpdateInput(UpdateCommand[] commands, UpdateOutcome outcome)
{
    public UpdateCommand[] Commands { get; } = commands;

    public UpdateOutcome Outcome { get; } = outcome;
}
