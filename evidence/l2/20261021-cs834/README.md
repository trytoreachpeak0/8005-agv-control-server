# control-server#834 evidence (CI real rig, 2026-10-21)

Control-server `24e7f55dd`, simulator `fb5f7c5`. Each scenario run keeps `SUMMARY.md` and `assertions.json`; the full
artifacts (logs, snapshots, timeline) are on the CI runs.

| Directory | Run | Onboard | Scenarios | Verdict |
| --- | --- | --- | --- | --- |
| `green-ci-39552233499-hmi390-b0cd48f` | 39552233499 | onboard-hmi PR #390 head `b0cd48f` (hmi#385) | load-door-closed-empty-reopens, unload-not-emptied, expected-action-overdue x3, slot-fault-declaration x3 | 8/8 PASS |
| `red-ci-39552278803-old-onboard-2f2ef45` | 39552278803 | `2f2ef45` (before hmi#385, reopens by itself) | load-door-closed-empty-reopens, unload-not-emptied, expected-action-overdue x3 | 5/5 FAIL |
| `red-ci-39553412432-mutation-no-declaration-abort-27c5fa2` | 39553412432 | `27c5fa2` = `b0cd48f` without `active.Abort.CancelAsync()` in `DeclareSlotFaultAsync` (branch `w2g/cs834-red-no-declaration-abort`, not for merge) | slot-fault-declaration x3 | 3/3 FAIL |

## Green

- Every shut over the opposite occupancy: lock latched 0.2 s after the close, no UNLOCKING for 5 s (HeldShut), then
  「重新开门」 pressed, one UNLOCKING, door OPEN with output reset, WAITING_OPERATOR again.
- L2-EAO-03: raisedAt -0.02..-0.03 s from first unlock + threshold, -13.5..-13.7 s from the pressed reopen + threshold.
- L2-RSFD-07 (all three): no 「重新开门」 for 10 s after the shut, door CLOSED/EMPTY/1/0 throughout, UNLOCKING 1 = before.

## Red, old onboard

The red lands on HeldShut in every round: the door popped open 0.6-2.1 s after the close and UNLOCKING rose by one
with nobody pressing (L2-DC-02/04/05, L2-UE-03/04, L2-EAO-01/13). The scenarios ran to the end because the helper
waits for the vehicle's own reopen instead of pressing.

Round 01 of expected-action-overdue also failed L2-EAO-04 and L2-EAO-14 (the alarm and the snapshot request crossed
connection #2, two connections in the run: the session reconnected once). Rounds 02 and 03 and all three green rounds
pass both; it is not what this ticket changed.

## Red, mutation

L2-RSFD-07 (all three): 「重新开门」 offered 0.6-1 s after the shut, pressed, UNLOCKING 1 -> 2. The door stayed shut
(the pulse is refused at the declaration gate after the progress is sent), so the old "door still closed" criterion
alone would have passed. L2-RSFD-03/04/05 are red too, as expected without the abort (no OperationResult).
