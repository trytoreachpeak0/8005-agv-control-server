# control-server#802 evidence

All runs: `dotnet test tests/ControlServer.Tests -c Release --filter ...` through `Invoke-HeavyLocal.ps1`, on the laptop, 2026-10-21.

- `red/red-run.txt` -- `UnloadedSlotAvailabilityTests` on the test commit `ea87f81b3` (base `fp/v2-impl@7bf593fef`, no product change): 4 red, 14 green.
  The four red are the ones the ticket names: the 10-21 agv02 replay reads `3,4,8` where `1..8` is expected; a demand refused for
  capacity stays refused after the same session unloads front 1-4; slots got back are not there after the session was not ready
  on the server's own order; a slot got back is not the one a new sublot is assigned (`[3,4]` instead of `[1,2]`).
- `green/targeted.txt` -- after the fix `1ec02a0bd`: `UnloadedSlotAvailabilityTests`, `JourneyRuntimeWorkerAdmissionTests`,
  `SlotAssignmentAtScanTests`, `*ArchitectureTests`, `*SlotCapacity*`, `*Staging*`, `*LoadingPhase*`,
  `PickupDispatchPlanPastOwnOrderTests`: 439 passed.
- `green/class-after-sharpen.txt` -- the class again after `86ea3ebea` sharpened two tests: 18 passed.
- `mutations/` -- one mutation at a time of `OnboardDispatchFactsReader.cs` (the `.diff` beside each), the class run against it.
  `summary.txt` lists which test went red. `no-unload-type` and `any-baseline-slot` survived the first pass; the tests were
  sharpened (`86ea3ebea`) and the `-2` runs show them killed. The summary's first line after the first block is a repeat of
  `no-time-order` from the second script's header, run against the same code.
