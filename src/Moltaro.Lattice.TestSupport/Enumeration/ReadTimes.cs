namespace Moltaro.Lattice.TestSupport.Enumeration;

// The time limit of a read (LatticeDatabaseOptions.ReadTimeLimit) for the tests that need a step to stay
// within it and the limit to pass elsewhere, and the time that passes it. A step that does not wait
// inside the engine takes microseconds, but a machine busy with other work can hold one up for longer
// than a short limit: under a soak that kept every processor busy, the first step of a pass of ten
// entities spent more than 100 ms inside the engine. So these tests set Limit, and let the limit pass
// only for as long as they control: a pause of the caller between two steps, the wait for the position
// of a session, or a wait inside a step that they hold, each lasting BeyondTheLimit. The margins are of
// time, not certainties: a step that has to stay within the limit fails only if the machine holds it up
// for two seconds inside the engine, and a step that has to pass the limit by a wait the test holds
// passes it only if it takes its timestamp within a second of the moment the test starts to count that
// wait, three seconds less two.
internal static class ReadTimes
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan BeyondTheLimit = Limit + TimeSpan.FromSeconds(1);
}
