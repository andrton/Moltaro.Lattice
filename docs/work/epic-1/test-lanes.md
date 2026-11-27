# Test lanes

| Lane | Contains | Runs locally | Runs in CI |
| --- | --- | --- | --- |
| Default | Contract tests, engine component tests, master and children in one process over loopback TCP, with a port chosen by the system or reserved by the test when the address has to be known before the master opens, allocation tests | `dotnet test` from `src`; no service needed; stays within about a minute | The required `build` job, as today; a second job runs the same lane on Windows |
| PostgreSQL | `Moltaro.Lattice.PostgreSql.Tests`: provider, durable commit, base, recovery | With `LATTICE_TEST_POSTGRES` set to a connection string whose role may create databases. Every test class creates a database with a unique name and drops it. A test that restarts the server starts its own `postgres:18` container and needs Docker; without Docker it reports itself skipped with that reason | A job of its own on every pull request; it starts a `postgres:18` service container and sets the variable |
| Cluster | `Moltaro.Lattice.Cluster.Tests` starts `Moltaro.Lattice.TestNode` processes: one master and its children on loopback, with killing and reconnecting | With `LATTICE_TEST_CLUSTER=1` and the PostgreSQL lane enabled, because the master is Durable; three children by default | A job of its own on every pull request with three children; 5 and 10 children in a job started by hand |
| Ordering stress | Tests of the engine that state an order of stores readers that take no lock depend on, such as `Engine/Batches/Revisions/MemberRevisionUnderReadsTests`: writers and readers at full speed, every value read checked against one some write produced | With `LATTICE_ORDERING_STRESS=1`; about 11 seconds. On x64 it passes whatever the order of stores, since x64 keeps them in order; it can fail only on hardware that orders stores weakly | The `arm64` job sets the variable on every pull request; the rented ARM64 machine runs it as well in a start that changes the order of stores |
| Measurements | `Moltaro.Lattice.Measurements`, a console program: the fixture, the managed reference, the load profiles, the check of the machine. Not a test project | Started by hand with a manifest, the nodes in containers with limits; raw output under the ignored `src/.local` | A job started by hand, to see that it runs; a gate is judged only on the reference machine |

- A lane whose variable is absent reports its tests as skipped with the reason "the lane
  is not enabled". That is a missing prerequisite, not a hidden failure: CI always
  enables both lanes, and the coordinator verifies a sprint with the PostgreSQL lane enabled from S27 on, and
  with the cluster lane as well from S35 on.
- The default lane runs in three jobs: `build` on Linux x64, `windows` on Windows x64 and
  `arm64` on Linux ARM64, a runner of four processors. The third was added on the
  owner's decision of 19 November 2026: reads take no lock and a write is published by
  one store, and the memory ordering of x64 hides a class of faults that ARM64 does
  not. A failure on ARM64 that the other jobs do not show is a finding about the engine
  first: it is reported with the name of the test before anyone changes the test.
  Longer race runs on a machine with many ARM cores are a later step.
- **Tests that depended on the load of the machine (19 November 2026).** Ten repeats of
  the suite in Release beside three other builds and suites on the workstation had three
  failures in replication tests, and twelve repeats on a quiet workstation none. Three
  tests raced one second of wall time and were given a hold the test controls or a wait
  of 5 s: `OpenTimeoutTests.Load_that_takes_longer_than_the_OpenTimeout_while_its_parts_keep_arriving_becomes_ready`,
  `IdleMasterTests` and `SilentPeerTests`; each then passed 50 of 50 under a loaded
  machine. The evidence is the margin of each wait, about 0.5 s before and at least 4.5 s
  now: the load of the author did not break the old tests either, so no failure was
  reproduced and then cured. Two failures stay unexplained, with what was ruled out: a
  master that could not listen, "the system lacked sufficient buffer space" (the suite
  holds about 28 listeners at its peak and leaves none behind a passing test; nothing is
  retried, in the engine or in a test), and one failure of `AnnouncedLengthTests` in the
  handshake, which has no short wait. The output of every repeat is kept, so that the
  next one has a name and a message.
- **What became of it (closed on 20 November 2026).** By the rule of the owner nothing
  open is handed on, so each item states its result:
  - *A handshake that failed once.* `AnnouncedLengthTests.Request_whose_frames_hold_more_than_it_announced…`
    failed in the handshake in one run of the engine tests in Debug on 19 November 2026,
    while two other agents built and ran suites on the workstation, and passed three of
    three alone; its message was not kept. Ruled out: a short wait in the test (it has
    none: bounds of one minute, timeouts of 30 s). Done: a soak of 500 runs of the
    replication tests of the engine, 244 tests a run, 250 runs in Debug and 250 in
    Release, four loops in parallel beside burners on every logical processor, the output
    of every run kept. It did not appear. Closed as not reproduced in 500 runs under load;
    nothing is retried anywhere.
  - *A master that could not listen.* `ClosedFrameTests.Closed_that_is_not_valid_ends_the_connection_and_a_request_without_a_reply_in_IndeterminateOutcome`
    failed once in ten repeats in Release on the same day under the same load: the bind
    of the listening socket reported that the system lacked sufficient buffer space or a
    queue was full. Ruled out: a leak of the tests (about 28 listeners at the peak of the
    suite, none left behind a passing test) and the range of ports (510 connections
    against about 14,000 ports under the heaviest load). Suspected and not shown: a
    shortage of the system as a whole (the commit charge stood at 59 of 72 GB under
    load). It did not appear again in the soak. The helpers through which tests open a
    listening master now report, when a socket error ends the open, the state of the
    system: the socket error, the commit charge and its limit, the available memory, the
    handles and threads of the process, and the listeners and connections of the machine
    (`ListenFailure` and `SystemState` in `Moltaro.Lattice.TestSupport`). It is diagnosis
    only and nothing is retried, so that a recurrence explains itself. Closed as a
    shortage of the system under load, instrumented.
  - *A forwarded write the child did not apply in ten seconds.* `CancellationTests.Write_the_sender_has_not_taken_is_withdrawn_by_its_cancellation_and_never_executed`
    failed once in five repeats in Release on 19 November 2026, on the engine of `main`
    (the test and the engine unchanged by the change above), while another agent was
    building and measuring on the workstation: "The write is committed by the master at
    position …-1, but this child has not applied it: This node did not reach position
    …-1 within 00:00:10", from `Forwarder.WaitForApplicationAsync`. Ten seconds is long
    for a loaded machine, so this was examined as a defect of the engine first: a child
    that does not apply, or is not woken for, the first transaction after its load. No
    defect of the engine was found. The write that reported it was the setup write of
    32 MiB, which the sender had already taken, not the cancelled one: the withdrawal
    worked. Read with their lines and ruled out: a write between the end of a child's
    load and the start of its sender; the wake of the sender; a large transaction waiting
    in its middle; a follower that stopped, which would end the waiter at once with "lost
    its master"; a lost wake-up of the waiter in `PositionClock`, because the waiter
    publishes its target before it reads the sequence and the publication is followed by
    a full fence before the targets are read; a waiter for a position equal to the one
    just reached, which is woken; the withdrawn call left in the sender's queue, which is
    skipped. On the engine of `main`, 3,000 forwarded writes that each waited for its own
    application and 3,000 waits for the exact committed position all passed, and the test
    with the child's application held for 11 s fails with the identical message. Not
    reproduced in 484 runs: 200 of 200 alone, 259 of 259 beside 20 burners and a loop of
    the engine suite, 25 of 25 inside runs of the suite. On an idle machine the
    transaction arrives whole about 170 ms after the wait begins, so the failure needed a
    stall some sixty times longer; what stalled that one run is not shown. The defect was
    the test's: it put a write of 32 MiB under the default synchronization timeout of
    10 s, which is not what the test is about. Corrected: the tests whose setup is large
    (`CancellationTests`, `AcknowledgmentDuringARequestTests`, the test of 32 MiB of
    `MasterAndChildTests`, `ChildIndexTests`) open their child waiting beyond the bound,
    and the forwarding tests among them that put a write of 32 MiB through the child,
    `AcknowledgmentDuringARequestTests` and both tests of `CancellationTests`, report the
    child's applied position and the state of the system when that wait ends by a timeout
    (`ApplicationWait`).
  - *A read time limit of 100 ms against the machine.* `ReadLimitsTests.Cancellation_of_an_enumeration_under_read_limits_is_OperationCanceledException_and_never_ReadLimitExceeded`
    failed once with the workstation at full load, and
    `ReadTimeInsideTheEngineTests.Step_that_waits_inside_the_engine_longer_than_the_time_limit…`
    twice in 25 runs of the suite. The engine was right: it checks the token before every
    limit, and the step that failed was one whose token was not cancelled. The tests of
    the read time limit (`ReadLimitsTests`, `ReadTimeLimitTests`, `CancelledStepTests`,
    `ReadTimeInsideTheEngineTests`) now use times the test controls, kept once in
    `Moltaro.Lattice.TestSupport` (`ReadTimes`: a limit of 2 s, controlled waits of 3 s).
    The review showed with throwaway changes of the engine that they still catch what they
    exist for: counting the caller's time fails both tests of the limit; not counting the
    wait for a slot fails both tests of the time inside the engine; a cancelled wait that
    reports the limit first fails exactly the two cases meant for it. One order had no
    test, the token before the limits inside a round; it has one now, which fails ten runs
    of ten with the two checks swapped in the engine.
  - *An Applied ahead of the send that the master accepted.* `AppliedTests.Master_ends_the_connection_of_a_child_that_acknowledges_a_transaction_whose_last_frame_it_has_not_been_sent`
    failed once in the 500 runs of the soak: the master did not end the connection within
    the bound of one minute. The engine was right. Forced both ways on the engine as it
    is: a peer whose receive buffer is 64 MiB takes all 16 MiB with the last frame, so the
    master has queued the last frame, accepts the Applied and keeps the connection, which
    is the failure, deterministic; a peer whose receive buffer is fixed at 64 KiB before
    it connects holds 64 KiB, and the master ends the connection. The test rested on how
    much the loopback buffers of the kernel happen to take. That the window grew in the
    failing run is inferred, not reproduced: the default stayed 64 KiB in 20 of 20 tries
    under load. Corrected at the two helpers every such test goes through: `Peer` and
    both sockets of `LoopbackLink` fix their receive buffer at 64 KiB before they connect.
    Fifty runs of the replication tests under load followed, and none failed. On Linux a
    send buffer may grow to 4 MiB, so the cases of 8 MiB have a margin of about two there.
    Left, with its place: the first test of `ChildCloseTests` depends on the receive
    buffer of the engine's own child and has a margin of two over the largest window of
    Windows (16 MiB); S33 gives it a form that does not rest on it.
  - Known and left: `SilentPeerTests` asserts only that the master does not give up
    before its timeout; its upper limit is the bound of one minute.
  - The evidence for the rewritten tests is the margin of each wait and the forcing
    experiments, not a failure reproduced and then cured, except where it says
    "deterministic".
- The `build` job keeps its name, because the ruleset of `main` requires that check. The
  PostgreSQL job and the cluster job become required checks too: the sprint that adds a
  lane adds its job to `.github/rulesets/main.json`, and the owner applies the ruleset.
- A crash test never kills a process the test run did not start, and never stops the
  PostgreSQL server of the machine; a server restart is tested against a container the
  test owns.
- Each project is created by the first sprint that needs it: the measurement program in
  S05, the PostgreSQL tests and the node program in S27, the cluster tests in S35.
- Measurements use a plain bounded harness, as the prototypes do; no benchmark package is
  added without the owner's decision.
