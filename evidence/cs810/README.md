# control-server#810 evidence

`Get-L2PeerPublish` (`scripts/l2/L2.psm1`): two runs publishing the same peer commit into the same cache root
no longer delete each other's build. Self-check: `scripts/l2/Test-L2PeerPublishCache.ps1`.

## red/

- `01-selfcheck-before-fix.txt` — the self-check on `f0a81a51`/`5a74354f` (test commit: the build moved behind
  `-PublishCommand`, behaviour otherwise unchanged). 10 assertions fail. The decisive ones: the second run does
  not queue but starts building (`queued=False building=True`), the first run's marker is gone from its own
  build directory while the second is there (`present=False`), the commit is built twice (`builds=2`), and the
  published directory holds the second run's build. A failed build leaves its cache directory behind; a waiter
  never queues behind a holder; `-LockTimeoutSeconds` does not exist yet.
- `02`–`07-mutation-*.txt` — on the fix commit, one change to `L2.psm1` each, the self-check re-run, the file
  restored with `git checkout`. Every mutation turns at least one assertion red:

  | mutation | red assertion(s) |
  | --- | --- |
  | M1 no `publish.ok` re-check after taking the lock | 1 built exactly once; 1 publish is the first run's; 1 FromCache; 5 |
  | M2 a failed build keeps its temporary directory | 2 the failed build leaves nothing |
  | M3 dead runs' temporary directories are not swept | 3 the dead run's temporary directory is cleared |
  | M4 lock name without the cache-root hash | 7 a run on another cache root does not wait |
  | M5 an abandoned mutex is not taken over | 3 the waiter builds after the holder died; 3 cleared |
  | M6 an unstamped cache directory is not moved aside | 6 an unstamped cache directory is rebuilt |

  Not covered by a mutation: writing `publish.ok` after the rename instead of before. The window it opens
  (directory present, stamp not yet written) is a few microseconds and no deterministic test holds it open;
  the order is argued in the code comment and the PR body instead.

## green/

- `01-selfcheck-after-fix.txt` — all 27 assertions of the 8 cases pass.
- `02-test-selfcheck-scope.txt` — `scripts/Test-TestSelfCheckScope.ps1` with the new step registered in
  `scripts/TestSelfCheckScope.psd1` (key `l2_peer_publish_cache`): passed.
- `03-real-publish-old-vs-new.txt` — a real `dotnet publish` of onboard-hmi `2b25aa5d` and slots-simulator `fb5f7c59`,
  once with the old `L2.psm1` (`origin/fp/v2-impl@4dda6733`) and once with the new one, each into a fresh cache root.
  Same layout (`clone/`, `publish/`, `publish.ok`), same path to `publish/`, the same 19 and 11 files; a second
  call with the new code reuses (`FromCache=True`). The longest path under the cache root is 277 characters in
  both; the temporary directory adds 13 while building, and the build succeeded.
- `04-real-publish-control.txt` — why 12 and 6 of those files differ in bytes: the old code publishing the same
  commits a second time, into another root, differs in exactly the same files. The difference comes from building
  in a different directory, not from this change.
- `05-synthetic-normal-load/` — one synthetic L2 round (`normal-load`, port slot 5) on the fix commit: PASS. A
  regression check only: synthetic scenarios do not call `Get-L2PeerPublish` (`scripts/l2/README.md`, real onboard only).
- `06-ci-real-rig-39471104014.txt` — CI real rig `real-onboard-normal-load` ×1 on the fix commit: success. The onboard was
  built through the new lock and rename; the simulator reused a cache directory the old code had written on the runner.
