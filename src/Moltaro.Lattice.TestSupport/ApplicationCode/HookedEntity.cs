namespace Moltaro.Lattice.TestSupport.ApplicationCode;

// An entity class whose constructor and whose set accessor of Text run code that a test gives: the
// code of the application that a read runs when it builds an object of the class. A schema binds it
// to an entity type with the key Id, an Int64, and the String field Text.
//
// A test arms a hook for the flow it runs in: the hooks are AsyncLocal, so tests that run at the same
// time do not see each other's, and a read that a test starts on a thread of the pool sees the hook
// the test armed in that task. A hook runs once: it is disarmed before it runs, so a read it makes
// builds its objects without it.
internal sealed class HookedEntity
{
    private static readonly AsyncLocal<Action?> OnConstruction = new();

    private static readonly AsyncLocal<Action?> OnSetText = new();

    private string _text = "";

    public HookedEntity()
    {
        Run(OnConstruction);
    }

    public long Id { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            Run(OnSetText);
        }
    }

    // The next object of this class that is constructed runs the hook in its constructor.
    public static void ArmConstruction(Action hook) => OnConstruction.Value = hook;

    // The next set of Text runs the hook, after the value is set.
    public static void ArmSetText(Action hook) => OnSetText.Value = hook;

    private static void Run(AsyncLocal<Action?> hook)
    {
        var code = hook.Value;

        if (code is null)
        {
            return;
        }

        hook.Value = null;
        code();
    }
}
