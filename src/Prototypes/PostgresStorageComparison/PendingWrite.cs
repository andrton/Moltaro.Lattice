namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class PendingWrite
{
    public PendingWrite(Mutation mutation, int commandBytes)
    {
        Mutation = mutation;
        Command = new byte[commandBytes];
        QueuedWriteCodec.Fill(mutation.Sequence, Command);
    }

    public Mutation Mutation { get; }
    public byte[] Command { get; }
    public TaskCompletionSource<long> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
