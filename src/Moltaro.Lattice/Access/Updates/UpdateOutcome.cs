namespace Moltaro.Lattice.Access.Updates;

// Every result of one update shares this state. The completion is the task the
// caller awaits. Bits prepared by an attempt can be read only after it succeeds.
internal sealed class UpdateOutcome
{
    private Task<LatticeWriteResult>? _completion;
    private byte[]? _bits;

    public void Attach(Task<LatticeWriteResult> completion) => Volatile.Write(ref _completion, completion);

    public void SetBits(byte[] bits) => _bits = bits;

    public bool Changed(int command)
    {
        if (Volatile.Read(ref _completion) is not { IsCompletedSuccessfully: true })
        {
            throw Unavailable();
        }

        return (_bits![command / 8] & (1 << (command % 8))) != 0;
    }

    public static byte[] BitsFor(int commands) => new byte[(commands / 8) + (commands % 8 == 0 ? 0 : 1)];

    public static void SetChanged(byte[] bits, int command) => bits[command / 8] |= (byte)(1 << (command % 8));

    public static InvalidOperationException Unavailable() => new("The command has no successfully completed submission.");
}
