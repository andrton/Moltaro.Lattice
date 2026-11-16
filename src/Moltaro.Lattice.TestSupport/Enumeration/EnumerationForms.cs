using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.TestSupport.Enumeration;

// The four forms of an enumeration of one entity type, so that one test states a behavior of each:
// through the class of the entity type or by name, synchronous without a session or asynchronous with
// one (ILatticeSet<TEntity>.Enumerate and EnumerateAsync, and those of ILatticeSet), each under the
// read limits of the options of its database.
//
// A form is given as an asynchronous sequence of the items a test compares, which the test's selectors
// make from each entity as its step returns it. Every step of the sequence is exactly one step of the
// enumeration, on the thread that takes it: a synchronous form calls MoveNext from MoveNextAsync. So a
// test that steps through the sequence steps through the enumeration, code it runs between two steps
// of the sequence runs between two steps of the enumeration, and a step that fails throws from
// MoveNextAsync. The sequence is no iterator of C#, which ends at the first exception it meets: a step
// after one that threw is a step of the enumeration again, so that a test can state what a further
// step does. The asynchronous forms take a new session that depends on no position.
internal static class EnumerationForms
{
    public const string ByClass = "by class";

    public const string ByName = "by name";

    public const string ByClassWithASession = "by class with a session";

    public const string ByNameWithASession = "by name with a session";

    public static IReadOnlyList<string> All { get; } = [ByClass, ByName, ByClassWithASession, ByNameWithASession];

    public static IAsyncEnumerable<TItem> Of<TEntity, TItem>(
        string form,
        ILatticeSet<TEntity> classes,
        ILatticeSet records,
        Func<TEntity, TItem> fromEntity,
        Func<LatticeRecord, TItem> fromRecord)
        where TEntity : class => form switch
        {
            ByClass => new SynchronousSteps<TEntity, TItem>(classes.Enumerate(), fromEntity),
            ByName => new SynchronousSteps<LatticeRecord, TItem>(records.Enumerate(), fromRecord),
            ByClassWithASession => new AsynchronousSteps<TEntity, TItem>(classes.EnumerateAsync(new LatticeSession()), fromEntity),
            ByNameWithASession => new AsynchronousSteps<LatticeRecord, TItem>(records.EnumerateAsync(new LatticeSession()), fromRecord),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "No such form of an enumeration."),
        };

    // A synchronous enumeration, one MoveNext for each MoveNextAsync.
    private sealed class SynchronousSteps<TEntity, TItem> : IAsyncEnumerable<TItem>
    {
        private readonly IEnumerable<TEntity> _source;

        private readonly Func<TEntity, TItem> _select;

        public SynchronousSteps(IEnumerable<TEntity> source, Func<TEntity, TItem> select)
        {
            _source = source;
            _select = select;
        }

        public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(_source.GetEnumerator(), _select);

        private sealed class Enumerator : IAsyncEnumerator<TItem>
        {
            private readonly IEnumerator<TEntity> _steps;

            private readonly Func<TEntity, TItem> _select;

            public Enumerator(IEnumerator<TEntity> steps, Func<TEntity, TItem> select)
            {
                _steps = steps;
                _select = select;
            }

            public TItem Current { get; private set; } = default!;

            public ValueTask<bool> MoveNextAsync()
            {
                if (!_steps.MoveNext())
                {
                    return ValueTask.FromResult(false);
                }

                Current = _select(_steps.Current);

                return ValueTask.FromResult(true);
            }

            public ValueTask DisposeAsync()
            {
                _steps.Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }

    // An asynchronous enumeration, one MoveNextAsync for each.
    private sealed class AsynchronousSteps<TEntity, TItem> : IAsyncEnumerable<TItem>
    {
        private readonly IAsyncEnumerable<TEntity> _source;

        private readonly Func<TEntity, TItem> _select;

        public AsynchronousSteps(IAsyncEnumerable<TEntity> source, Func<TEntity, TItem> select)
        {
            _source = source;
            _select = select;
        }

        public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(_source.GetAsyncEnumerator(cancellationToken), _select);

        private sealed class Enumerator : IAsyncEnumerator<TItem>
        {
            private readonly IAsyncEnumerator<TEntity> _steps;

            private readonly Func<TEntity, TItem> _select;

            public Enumerator(IAsyncEnumerator<TEntity> steps, Func<TEntity, TItem> select)
            {
                _steps = steps;
                _select = select;
            }

            public TItem Current { get; private set; } = default!;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (!await _steps.MoveNextAsync())
                {
                    return false;
                }

                Current = _select(_steps.Current);

                return true;
            }

            public ValueTask DisposeAsync() => _steps.DisposeAsync();
        }
    }
}
