using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A child can still finish work under Writes after publishing its position.
// Retry admission only. The first admitted operation supplies the test's result.
internal static class MaintenanceStep
{
    public static async Task<T> RunAfterAdmissionAsync<T>(EngineDatabase node, Func<DatabaseParts, T> operation)
    {
        using var stop = new CancellationTokenSource(Bound);
        T observed = default!;
        Action<DatabaseParts> run = parts => observed = operation(parts);
        while (!node.TryRunMaintenanceStep(run))
        {
            stop.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        return observed;
    }
}
