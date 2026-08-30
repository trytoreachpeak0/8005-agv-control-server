# Snapshot redelivery order against the peer's monotonic revision rule

Planning ticket: `8005---AGV` `.scratch/wire-to-gate-ai-implementation-kit/issues/23-settle-snapshot-redelivery-order-against-monotonic-revision.md`
Product under test: `ControlServer_MVP` at the commit this evidence is committed with
Peer read read-only from `OnboardHmi_MVP@304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6` (`git show origin/OnboardHmi_MVP:<path>`, no checkout, no write)
Protocol: `protocol-v0.1.1`

## Question

The server replays every unacknowledged outbox row at the top of each runtime iteration, oldest
first (`JourneyRuntimeEngine.AdvanceAsync` -> `OnboardJourneyPublisher.ReplayPendingForSessionAsync`,
ordered by `ProtocolOutbox.CreatedAt`). One journey publishes each of the three journey snapshot
types twice: once at the pickup and once at the gate, under a higher revision. The peer keys an
adopted snapshot on its message type alone and refuses a lower revision as
`SNAPSHOT_REVISION_REGRESSION`.

So: if a pickup snapshot's `SnapshotAppliedAck` is lost while the gate snapshot at the higher
revision is adopted, does the pickup row's replay arrive as a regression and tear the session down?
Is that reachable against the real peer, and if so, who owns the fix?

## Verdict

**Not reachable against `OnboardHmi_MVP@304e6ad`. No product change was made.**

The hypothesis missed that on that peer **the replay is also the repair**. In
`WireToGateSessionClient`, applying a snapshot, journalling it and answering it are three sequential
statements with no early exit between them:

```
(long revision, string snapshotKind) = ApplyJourneyProjection(envelope, payloadContentSha256, ct);
await _journal.SaveAppliedJourneySnapshotAsync(..., ct);
await SendSnapshotAppliedAckAsync(..., ct);
```

`ApplyJourneyRevision` returns early for a duplicate at the revision it already holds, but that early
return is inside a `void` helper -- the journal write and the acknowledgement still run. A duplicate
is therefore **acknowledged again**. Because the server re-sends every pending row once per runtime
iteration, one lost acknowledgement is settled by the next iteration, long before the gate allocates
the higher revision.

## Observations

`JourneyRuntimeWorkerTests`, driving one journey to the gate against `AdoptingPeer` -- a peer model
that reproduces the rule quoted above: an adopted revision journalled under its message type alone
and never forgotten, a lower revision refused and never acknowledged, a duplicate acknowledged
again. Acknowledgements are buffered per iteration so a test can lose exactly the ones a peer had in
flight when its connection dropped.

| Harness | Result |
| --- | --- |
| `ALostAcknowledgementNeverLeavesASupersededSnapshotToBeRedelivered` (9 cases) | PASS 9/9 |
| `TheRedeliveredRevisionNeedsEveryAcknowledgementFromThePickupToTheGateLost` (3 cases) | PASS 3/3 |
| `OnlyAPeerThatNeverAcknowledgesIsShownASupersededSnapshot` | PASS 1/1 |

Console output: `observation-run.log`.

1. **Every single-iteration acknowledgement loss is clean.** Losing the whole in-flight batch at any
   one of the nine iterations -- including the gate iteration itself -- produces no snapshot below
   the revision the peer holds. Iteration 0 loses nothing and is the control.
2. **The green is not vacuous.** Each run asserts the peer actually reached the dangerous state: all
   three snapshot types adopted at two distinct revisions, so the pickup rows and the gate rows both
   existed while the peer held the gate revision.
3. **The detector fires.** Against a peer whose acknowledgements never arrive at all, every one of
   the three streams regresses, and the shape is exactly the hypothesis: revision 1 delivered while
   the peer holds 2, for `VehicleBusinessStateSnapshot`, `CurrentStopWorklistSnapshot` and
   `UpcomingStopPlanSnapshot`.
4. **The boundary is five consecutive iterations.** Losing iterations 3 through 7 is red; losing 4
   through 7, or 3 through 6, is green. A single successful delivery anywhere in that span settles
   the pickup rows.

## Why that boundary cannot occur on the wire

The span the loss has to cover is exactly the span in which the journey consumes `SublotSubmitted`
and `PreDepartureSafetyCheckResult`. Those travel the same peer-to-server direction as the
acknowledgements, over the same connection. A peer whose acknowledgements never arrive for five
consecutive iterations, while its business answers do, does not exist on that transport. The fixture
supplies both answers by writing the inbox directly, which is what lets case 3 above advance to the
gate at all -- and that is the artifact behind the interleaved `1, 2, 1, 2` redelivery seen while
closing the revision *allocation* defect. That interleaving was a never-acknowledging fixture peer,
not a reachable sequence.

## Residual risk, named and owned

The server's correctness here rests on a peer behaviour the protocol does not require: that a
duplicate snapshot at the revision already adopted is acknowledged rather than silently ignored.
`protocol-v0.1.1` does not oblige a peer to answer a duplicate. A conforming peer that answered a
duplicate with silence would leave the pickup row pending forever, and the gate revision would then
make its replay a `SNAPSHOT_REVISION_REGRESSION` -- reachable, and a self-sustaining reconnect loop,
because the peer's journal never forgets and the outbox row is never fenced.

The server-side guard, if it is ever wanted, is the one that already exists for the recovery stream:
`OnboardRecoveryCoordinator.QueueSessionSnapshotAsync` fences the pending rows a new snapshot
supersedes. Generalising that to the journey snapshot types -- fencing a pending row of the same
message type and vehicle at a lower revision when a higher one is queued -- would close it without a
migration. It was **not** implemented: the defect is not reachable against the peer this release
binds to, and this is a release candidate.

Risk owner: whoever changes the peer's duplicate-acknowledgement behaviour, or binds this server to
a different onboard implementation. Either of those makes the guard above required before the
binding is used.

## Scope

Server product code, migrations and scripts unchanged; the only source change is
`tests/ControlServer.Tests/JourneyRuntimeWorkerTests.cs`. No vehicle movement, no order creation, no
RIoT call, no field credentials, no read-only-repository write.

Release build 0 warnings / 0 errors, `dotnet format --verify-no-changes` clean, tier 1
**243 passed / 0 skipped** (`tier1-run.log`; the SQL Server tests are not part of this suite).
