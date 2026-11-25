namespace Moltaro.Lattice.Access;

/// <summary>
/// Reads a protected view synchronously with caller-supplied state and returns a result
/// that can be kept after the call.
/// </summary>
/// <remarks>Keep the callback short. It must finish without performing I/O, waiting or calling the database.</remarks>
/// <typeparam name="TView">The type of view lent to the callback.</typeparam>
/// <typeparam name="TState">The type of state supplied by the caller.</typeparam>
/// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
/// <param name="view">The view, valid for the duration of this invocation.</param>
/// <param name="state">The caller-supplied state.</param>
/// <returns>The callback's result.</returns>
public delegate TResult LatticeViewFunc<TView, TState, TResult>(scoped TView view, TState state)
    where TView : allows ref struct;
