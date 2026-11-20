namespace Moltaro.Lattice.Measurements.Scenarios;

// One step of a thread of a window (StepWindow): a read, a search or a write, which the window calls
// over and over on a thread of its own. A step is a struct, and the window's loop takes it as a type
// argument, so that the loop is compiled for each kind of step and calls the step directly or inlines
// it: no call through a delegate or an interface is made for each step. Every step marks Run
// AggressiveInlining, because the JIT does not inline a step whose body throws on its own, and a step
// that is not inlined adds a call to each step. A step that throws does it in a method of its own:
// inlined, the text of the message would be set up on every step. What a step changes from one call
// to the next, such as its place in its keys, it keeps in its own fields.
internal interface IWindowStep
{
    // Takes one step and returns what it counts: one read or search, or the writes it made. counting
    // says whether the window is open, and counted what the thread has counted in it so far, for a step
    // that times some of its steps in the window.
    int Run(bool counting, long counted);
}
