namespace Moltaro.Lattice.Engine.Following;

// What the catch-up of a child applied before it was Ready (Follower.CatchUpAsync), for the measurement
// program (brief of Epic 1, sprint S11, the planner's decision of 17 November 2026 after the study,
// point 12): the capture start S and the readiness position R of its load, the roots its master sent, how long
// it took from LoadEnd to Ready, and of the states the transactions of (S, R] carried, how many the rule
// applied and how many it skipped because the copy held them already, or held nothing a Delete removes.
internal readonly record struct CatchUpReport(ulong CaptureStart, ulong Readiness, ulong Roots, TimeSpan Duration, long Applied, long Skipped);
