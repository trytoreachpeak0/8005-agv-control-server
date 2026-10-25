# control-server#865 evidence

- `red/l1-stale-snapshot-red.txt`, `red/l1-alarm-red.txt`: the test commits run on the code before each fix.
- `red/cs865-mut{A..G}.txt`: the seven mutations of the PR body, each caught.
- `red/l2-charging-door-open-at-handshake-then-locked/run1`: the synthetic scenario on `8f93cd65` (this branch with the
  transport fix reverted): L2-DOL-03 fails, `(none) | 1 UNLOCKED` -- the server still holds only the handshake's snapshot.
- `green/l2-charging-door-open-at-handshake-then-locked/run1`: the same scenario on `80266446`: PASS, one snapshot
  request after the lock, the vehicle sent to 211.
- `green/cs865-green2.txt`, `green/cs865-green3.txt`: local runs of the touched test classes.

The production database read (agv02 generation 50) is summarized in the PR body; no production data is kept here.
