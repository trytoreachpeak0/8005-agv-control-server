# control-server#834 evidence (CI real rig, 2026-10-21)

Control-server `24e7f55dd`, simulator `fb5f7c5`. Each scenario run keeps `SUMMARY.md` and `assertions.json`; the full
artifacts (logs, snapshots, timeline) are on the CI runs.

| Directory | Run | Onboard | Scenarios | Verdict |
| --- | --- | --- | --- | --- |
| `green-ci-39552233499-hmi390-b0cd48f` | 39552233499 | onboard-hmi PR #390 head `b0cd48f` (hmi#385) | load-door-closed-empty-reopens, unload-not-emptied, expected-action-overdue x3, slot-fault-declaration x3 | 8/8 PASS |
| `red-ci-39552278803-old-onboard-2f2ef45` | 39552278803 | `2f2ef45` (before hmi#385, reopens by itself) | load-door-closed-empty-reopens, unload-not-emptied, expected-action-overdue x3 | 5/5 FAIL |
| `red-ci-39553412432-mutation-no-declaration-abort-27c5fa2` | 39553412432 | `27c5fa2` = `b0cd48f` without `active.Abort.CancelAsync()` in `DeclareSlotFaultAsync` (branch `w2g/cs834-red-no-declaration-abort`, not for merge) | slot-fault-declaration x3 | 3/3 FAIL |

## After the review rework: green (`72180e86c`, onboard `968d213`)

`green-ci-39601806455-rework-968d213`, CI run 39601806455: 8/8 PASS, no onboard session timeout. Dispatched at 20:42:49 CST
once host CPU had stayed below 50 % for 5 minutes (18.2, 19.7, 17.7 % at 20:37-20:42, `host-cpu-before-dispatch.tsv`);
during the run it averaged 27 %, peak 85 % (`host-cpu-samples.tsv`).

- Every shut held (lock latched at +0.2 s, no UNLOCKING for 5 s) and reopened on the first press: L2-DC-02/04/05,
  L2-UE-03/04, L2-EAO-01/13.
- L2-EAO-02 read 9.8, 9.8 and 10 s before the threshold; the reopen came 8.5-8.6 s after the first unlock; L2-EAO-03
  -0.02 s from first unlock + threshold, -8.5..-8.6 s from the pressed reopen + threshold.
- L2-RSFD-07 (all three): no 「重新开门」 for 10 s after the shut, CLOSED/EMPTY/1/0, UNLOCKING 1 = before.

## After the review rework (`a30ddd724`, onboard `968d213`): two rounds red on rig disconnects, not on a criterion

`968d213` differs from `b0cd48f` only in the button's caption (`check-ui-layout.ps1`, one line each in
`OnboardController.cs` and its test). Both rounds below ran the same four scenarios with the same parameters.

| Directory | Run | Verdict | Onboard session timeouts ("上层会话不可用：The operation has timed out") |
| --- | --- | --- | --- |
| `rework-ci-39558343471-968d213-rig-disconnects` | 39558343471 | 5 PASS / 3 FAIL | 7 timeouts in 5 of 8 scenario runs |
| `rework-ci-39560869527-968d213-rig-disconnects` | 39560869527 | 6 PASS / 2 FAIL | 9 timeouts, all in slot-fault-declaration-01 |

For comparison, the first rounds had 0 (39552233499), 1 (39552278803) and 0 (39553412432).

**39558343471.** load-door-closed-empty-reopens-01 timed out waiting for the first WAITING_OPERATOR: the onboard dropped
the session 3 s after the first unlock and came back RECOVERY_REQUIRED, before this ticket's code ran.
expected-action-overdue-02: the press opened the door but the server never got the UNLOCKING (button gone, UNLOCKING 1->1,
OPEN/EMPTY/0/0 -- what review M3's actual column is for), and the scenario's guard threw. expected-action-overdue-03: three
connections, RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED, EAO-04..14 red with it. Host CPU was not sampled
during this round (82 % at 15:14 CST, just after it).

**39560869527.** Host CPU sampled every ~33 s (`host-cpu-samples.tsv`); heavy local runs were at 1 when it was dispatched.
slot-fault-declaration-01 timed out waiting for the Load command; its 9 timeouts fall at 15:26:00-15:27:12 CST, while the
host read 87-100 %. load-door-closed-empty-reopens-01 passed L2-DC-02/04/05 and failed only L2-DC-12 (next demand blocked
ONBOARD_SESSION_NOT_READY): the onboard's vehicle safety signal went stale (`VEHICLE_NOT_READY` with unknownPresent=True,
15:19:58) right after the cancellation; that code means the upstream signal was not refreshed in time, not a door fact.
Same at 15:18:53, before the load.

**What passed, both rounds.** Every criterion this ticket touched that the run reached: HeldShut and the press in
L2-DC-02/04/05, L2-UE-03/04, L2-EAO-01/13; L2-RSFD-07 on every slot-fault-declaration round that got past the Load.

**EAO margin after review M1** (first close at 3 s): L2-EAO-02 read 9.5 s and 8.5 s before the threshold (39558343471
rounds 01 and 03), 8.8 s, 9.6 s and 9.7 s in 39560869527 (all three EAO rounds passed there); the reopen came 8.5-9.7 s
after the first unlock, still above 3x tolerance.

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
