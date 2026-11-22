namespace Moltaro.Lattice.Tests.Indexes;

// A call refused as a mistake of its caller: it throws ArgumentException itself, never a type derived
// from it such as ArgumentNullException or ArgumentOutOfRangeException, since every refusal of an
// argument of an index is one (decisions on index access and on queries, Q07.3.6). The tests of the keys
// and the parts of an index state their refusals through it.
internal static class ArgumentRefusal
{
    // The call throws ArgumentException; what names the call it is, for the message of a failure.
    public static void Expect(Action call, string what) =>
        ExpectRefused(Record.Exception(call), what);

    // The same for an asynchronous call, whose refusal may come at the call or through its task.
    public static async Task ExpectAsync(Func<Task> call, string what) =>
        ExpectRefused(await Record.ExceptionAsync(call), what);

    private static void ExpectRefused(Exception? exception, string what) =>
        Assert.True(exception?.GetType() == typeof(ArgumentException), $"{what} was not refused with ArgumentException: {exception?.GetType().Name ?? "no exception"}.");
}
