# control-server#865 evidence

- `red/l1-stale-snapshot-red.txt`, `red/l1-alarm-red.txt`: the test commits run on the code before each fix.
- `red/cs865-mut{A..G}.txt`: the seven mutations of the PR body, each caught.
- `red/cs865-mutH-ask-only-when-ready.txt` (review S1): asking only when the store decides Ready turns
  `ASessionNotReadyWhenTheDoorIsLockedIsStillAsked` red, now that the store keeps that session RecoveryRequired.
- `red/cs865-mutI-change-reads-the-inbox.txt` (review S3): a change that reads the inbox's snapshots again turns
  `ASafetyChangeReadsNoSnapshotFromTheInboxOnceTheConnectionHasOne` red.
- `red/l2-charging-door-open-at-handshake-then-locked/run1`: the synthetic scenario on `8f93cd65` (this branch with the
  transport fix reverted): L2-DOL-03 fails, `(none) | 1 UNLOCKED` -- the server still holds only the handshake's snapshot.
- `green/l2-charging-door-open-at-handshake-then-locked/run1`: the same scenario on `80266446`: PASS, one snapshot
  request after the lock, the vehicle sent to 211.
- `green/cs865-green2.txt`, `green/cs865-green3.txt`: local runs of the touched test classes.
- `green/cs865-green4-after-review.txt`: after the review rework and the merge of `fp/v2-impl` `254bde775`.
- `green/cs865-probeD-timing.txt`: the review's per-change timing with 3000 other vehicles' snapshots, before and after S3.

The production database read (agv02 generation 50) is summarized in the PR body; no production data is kept here.
