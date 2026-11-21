using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// A search that a test holds inside the protection of reads at the roots it takes, through the switch of
// the engine (EngineDatabase.ArmReadSwitch, which a step of a search offers at each root it takes, before
// it reads it, as a read by key does once it has found its root): the search runs on a thread of its own,
// FindAsync taken in one call among them, and stops at the first root it takes until the test lets it go
// on, to the next root (NextAsync) or to its end (FinishAsync). The next root is the next one of another
// identity than the root held: a step that ends a round to make room outside the protection takes the same
// root again in its next round, and that is no step of the walk past it (Entities.EntityTable.TrySearch).
// Meanwhile the test writes, as a writer may while a read is inside. Every wait is bounded, and Dispose lets
// the search go, so that a test that fails before it finishes does not keep it.
internal sealed class HeldSearch<TResult> : IDisposable
{
    private readonly EngineDatabase _database;

    private readonly SemaphoreSlim _reached = new(0);

    private readonly SemaphoreSlim _release = new(0);

    private Task<TResult> _search = Task.FromResult(default(TResult)!);

    // The identity of the root the search was held at last; default before the first.
    private RootHandle _held;

    private HeldSearch(EngineDatabase database) => _database = database;

    // Starts the search and returns once it is held at the first root it takes.
    public static async Task<HeldSearch<TResult>> StartAsync(EngineDatabase database, Func<TResult> search)
    {
        var held = new HeldSearch<TResult>(database);

        database.ArmReadSwitch(held.Hold);
        held._search = OwnThread.Run(search);
        await held.HeldAsync();

        return held;
    }

    // Lets the search go on to the next root it takes and returns once it is held there.
    public async Task NextAsync()
    {
        _database.ArmReadSwitch(Hold);
        _release.Release();
        await HeldAsync();
    }

    // Lets the search go on to its end and returns what it found.
    public async Task<TResult> FinishAsync()
    {
        _release.Release();

        return await _search.WaitAsync(Bound);
    }

    public void Dispose() => _release.Release();

    // Runs on the thread of the search, inside the protection, until the test lets it go; at the root it
    // was held at last, taken again, it arms the switch again and goes on.
    private void Hold(RootHandle handle)
    {
        if (handle == _held)
        {
            _database.ArmReadSwitch(Hold);

            return;
        }

        _held = handle;
        _reached.Release();
        _release.Wait(Bound);
    }

    private async Task HeldAsync()
    {
        var reached = _reached.WaitAsync(Bound);
        var first = await Task.WhenAny(reached, _search).WaitAsync(Bound);

        Assert.True(first == reached && await reached, $"The search did not stop at a root it takes: {_search.Exception?.InnerException?.Message}");
    }
}
