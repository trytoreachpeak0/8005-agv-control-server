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
