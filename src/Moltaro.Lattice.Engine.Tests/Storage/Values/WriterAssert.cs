using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// Assertions on a refused call of a RootWriter, which Assert.Throws cannot make because its lambda
// cannot capture the writer; see WriterAction.
internal static class WriterAssert
{
    // The exception the call throws, or null when it throws none.
    public static Exception? Catch(ref RootWriter writer, WriterAction call)
    {
        try
        {
            call(ref writer);
        }
        catch (Exception exception)
        {
            return exception;
        }

        return null;
    }

    // The call throws an exception of exactly this type, which is returned.
    public static TException Throws<TException>(ref RootWriter writer, WriterAction call)
        where TException : Exception
    {
        var thrown = Catch(ref writer, call);

        Assert.True(thrown is not null, $"The call did not throw; {typeof(TException).Name} was expected.");

        return Assert.IsType<TException>(thrown);
    }

    // The call is refused with a LatticeException of this error, which is returned.
    public static LatticeException Refused(ref RootWriter writer, LatticeErrorEnum error, WriterAction call)
    {
        var exception = Throws<LatticeException>(ref writer, call);

        Assert.Equal(error, exception.Error);

        return exception;
    }

    // Complete is refused with a LatticeException of this error.
    public static LatticeException CompleteRefused(ref RootWriter writer, LatticeErrorEnum error) =>
        Refused(ref writer, error, static (ref RootWriter w) => w.Complete());
}
