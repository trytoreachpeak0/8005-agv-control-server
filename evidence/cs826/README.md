# control-server#826 evidence

All runs: `dotnet test tests/ControlServer.Tests -c Release --filter "FullyQualifiedName~PreDepartureAnswerClockSkewTests"`
(through `Invoke-HeavyLocal.ps1`), on this machine, 2026-10-21.

| Directory | Commit | What it shows |
| --- | --- | --- |
| `red/` | `dc475734` (tests only, option is an unread stub) | 9 of 11 fail on fp/v2-impl's judgment. The 2 that pass are the regression guards: an UNSAFE answer inside the tolerance still holds the vehicle. |
| `green/` | `da81fa9e` | The new class, `JourneyRuntimeWorkerDepartureSafetyTests`, `WireToGateStoreTests` and every `*ArchitectureTests` pass (133), then the new class alone after one more assertion (11). |
| `mutations/` | `da81fa9e` with one line changed back | Each mutation turns its tests red. |

Mutations (one file each, restored with `git checkout` after each run):

- M1 `observedAt > now + PeerClockSkewTolerance` -> `observedAt > now` (engine): the 0.12 s answer and the own-order test go red.
- M2 an outdated answer returns "invalid" at once instead of being passed over (the old "first answer decides"): all five
  `AnOutdatedFirstAnswerDoesNotHideALaterValidOne` cases go red, and the log assertion (the reason is no longer logged).
- M3 the first current answer is kept instead of the newest: `ALaterUnsafeAnswerOutranksAnEarlierSafeOne` goes red
  (an older SAFE would depart the vehicle) and the own-order test (the consumed answer is the older one).
- M4 the store's movement authorization back to zero tolerance: the 0.12 s answer throws
  `UnsafeMovementAuthorizationException` in the round.

## Review rework (#853 review M1, L1, L3)

- `red/review-tests-3cc442da.txt`: on `3cc442da` (the new tests over the reviewed code) the outdated newer UNSAFE (probe P1
  shape) and the UNSAFE received first but observed later (P3 shape) both let the SAFE depart, and 1001 / 2000 ms pass the
  validator (-1 ms fails only on the changed message).
- `green/bb949091.txt`: the same filter as above plus `JourneyRuntimeOptionsTests`, 156 passed.
- Mutations on `bb949091`, appended to `mutations/summary.txt`: M5 without the newer-unsafe veto both new tests go red; M6
  "newer" by receive time only, the P3-shape test goes red; M7 the bound back at 2 s, the 1001 and 2000 ms cases go red.
