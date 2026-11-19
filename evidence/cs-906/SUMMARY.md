# cs#906 evidence

Base: `fp/v2-impl` `20cdcf975` (cs#904 merged). Red commit `60d6c1503` (tests only), fix `c3b75cc02`,
append-exclusion test `a0c320397`. Raw logs: `C:/work/8005-scratch/cs906-mutations/`.

## Red before the fix

`60d6c1503` on the base product code: both new cases failed with the field exception
`Journey journey:10000000-0000-4000-8000-000000000001 waits for a load result with no demand loading at its stop.`

M0 (product `src/` checked out at `20cdcf975`, tests at `a0c320397`, `--no-incremental`): all three new cases red
with the same exception (`mutations/summary.txt`).

| Case | Red message on the base |
| --- | --- |
| `StagingLoadCancellationTests.ACancelledAppendedStagingLoadRedispatchedUnderAMovedBindingIsCarriedThroughWithAFreshEngineEachRound` | `InvalidDataException ... waits for a load result with no demand loading at its stop.` |
| `LoadEndedAtASecondPickupTests.AStopWaitingForALoadResultThatNothingExplainsIsHeldUnderItsOwnReasonUntilTheDatabaseIsPutRight` | `A round failed: ... waits for a load result with no demand loading at its stop.` |
| `LoadEndedAtASecondPickupTests.AJourneyHeldOnALoadResultNothingExplainsTakesNoAppendedDemand` | `InvalidDataException ... waits for a load result with no demand loading at its stop.` |

## Green after the fix (local, targeted)

- Staging*, LoadEnded*, LoadCancellation*, Batch7*, ReconnectModel*, *ArchitectureTests, JourneyStopSequence*, *Release*,
  MultiVehicleExecution*, EnRouteAppend*, StationYield*, *Recovery*, DispatchRound*, BlockedJourney*, *CargoHolding*,
  *Withdrawal*: 2267/2267 on `c3b75cc02` (3 min 7 s).
- After `a0c320397`: StagingLoadCancellationTests, LoadEndedAtASecondPickupTests, *ArchitectureTests, ReconnectModelTests,
  Batch7CargoHoldingDashboardTests: 206/206; the new append case and its control
  `MultiVehicleExecutionTests.AnInTransitVehicleTakesAnAppendedDemandIntoItsExistingJourney`: 2/2.
- `dotnet format --verify-no-changes` on the changed files: only the 14 whitespace findings `DispatchRoundRunner.cs`
  already has on the base (lines 811 and below; this change is lines 761-763); the other files clean.

## Mutations

Committed first; each mutation edits one checked line of one file, rebuilds with `--no-incremental`, runs
StagingLoadCancellationTests, LoadEndedAtASecondPickupTests, Batch7CargoHoldingDashboardTests, ReconnectModelTests,
JourneyStopSequenceRaceTests (148 cases), restores only that file (`mutations/run-mutations.sh`, diffs `mutations/M*.diff`).
M1-M10 ran on `c3b75cc02`; M11 and M0 on `a0c320397`.

| Mutation | What it undoes | Result |
| --- | --- | --- |
| M0 | the whole fix (`src/` at the base) | red: all three new cases, field exception |
| M1 | the self-healing resume gated on `IsStagingPickup` again | red: the L1 redispatch case |
| M2 | the unexplained shape throws every round again | red: the hold case |
| M3 | the code is never cleared after the database is put right | red: the hold case |
| M4 | the hold writes and logs every round | red: the hold case (two Error 2348) |
| M5 | engine lets the held vehicle into `underWay` | **survived** (the dispatch round's own exclusion holds) |
| M6 | dispatch round reads an en-route plan for the held journey | **survived** (the engine's exclusion holds) |
| M11 | M5 and M6 together | red: the append case (second demand appended); the hold case (route-graph invariant breach in that fixture) |
| M7 | release service releases from the held journey | **survived** (no case) |
| M8 | the code is not a wait on a person | red: `ReconnectModelTests.TheModelsWaitOnPersonCodesAreExactlyTheProductsSet` |
| M9 | readiness gate overwrites it with the session code | survived before the rework; red after it (M9b below) |
| M10 | no dashboard description | red: `Batch7CargoHoldingDashboardTests` for this code |

## Rework after review (PR #907 review M1, M2, L1-L3)

### M1: the readiness gate keeps the code (M9 now red)

New case `LoadEndedAtASecondPickupTests.AHeldLoadWaitKeepsItsReasonAndItsStartThroughASessionDrop` (`0ecc71a62`):
held on `LOAD_RESULT_WITH_NOTHING_LOADING`, the session drops to not ready for one round (the
`OwnOrderRebuildTests.DropSessionOnOwnOrderAsync` shape) and is restored; the code and `BlockReasonSince` stay the same
through both rounds and exactly one Error 2348 is logged.

M9b (`JourneyRuntimeEngine.cs:1457`, the gate's exclusion of this code, on `0ecc71a62`, `--no-incremental`,
LoadEndedAtASecondPickupTests): red, only the new case -- during the drop the code became the session code
(`mutations/M9b-gate-overwrites-with-session-case.diff`). M9 survived before this case.

### M2, L1: wording only (`c6c1c9f89`)

- `DemandReleaseService` skip: the reason is now "no plan revision while a person checks and repairs the journey"; the
  held stop's own demand is already refused by `DemandReleaseRules.Decide` (AfterArrival), so the skip only delays
  pending demands at later stops. No case (ruling).
- Dashboard text, event 2348 and remarks say "no new messages" instead of "no messages".

Targeted after the rework: StagingLoadCancellationTests, LoadEndedAtASecondPickupTests, *ArchitectureTests,
ReconnectModelTests, Batch7CargoHoldingDashboardTests, *Release*, BlockedJourney*: 949/949. `dotnet format
--verify-no-changes` on the four reworked files: exit 0.
