# cs#904 evidence

Base: `fp/v2-impl` `ab450961c`. Red commit `4ef3672c4` (tests only), fix commit `17bd5e83b`.

## Red before the fix

Both new cases in `StagingLoadCancellationTests` (fresh engine every round after the cancellation, as production's
per-iteration scope) failed on `4ef3672c4` with the field exception:

```
System.IO.InvalidDataException : Journey journey:10000000-0000-4000-8000-000000000001 waits for a load result with no demand loading at its stop.
```

## Green after the fix (local, targeted)

- `StagingLoadCancellationTests`: 19/19.
- Staging*, Batch7*, ArchitectureTests, JourneyStop*, LoadEnded*, EmptyPickup*, *Release*, *CargoHolding*, *LoadCancellation*, *Recovery*: 1923/1923 (13 min 58 s).
- `dotnet format --verify-no-changes` on the three changed files: exit 0.

## Mutations

Committed first; each mutation edits only `StagingPickupStops.cs`, rebuilds with `--no-incremental`, runs
`StagingLoadCancellationTests`, restores only that file (`mutations/run-mutations.sh`, diffs `mutations/M*.diff`).

| Mutation | What it undoes | Result |
| --- | --- | --- |
| M1 | per-round recognition (`RecognizeAsync`) counts live memberships only | red: both new cases, field exception |
| M2 | `CurrentStopIsStagingAsync` counts live memberships only | red: both new cases, `CurrentStopIsStagingAsync` assertion |
| M3 | `EverAtStop` drops removed memberships (both entries; the pre-fix behaviour) | red: both new cases, field exception |

Not mutated: a `LeftAtStop` that ignores the stop filter. It is equivalent in effect: recognition also requires a
pickup stop at the demand's bound staging station, so a removed staging membership from another stop can only make
a pickup at the same staging station staging, which it already is.

## Rework after review (PR #905 review M1, M2, L1, question 3)

### M1: revision checks only look at rows sent after the cancellation

Review M5 probe (`StopEndWorklist.cs`: `revision = 1;` before the stop-end worklist is staged,
`mutations/M5-stop-end-worklist-revision-1.diff`), on `d49fa7143` with `--no-incremental`, `StagingLoadCancellationTests`:

- `ACancellationDuringAnAppendedStagingLoadIsCarriedThroughWithAFreshEngineEachRound`: red,
  `A worklist sent after the cancellation went back to revision 1; the vehicle held 3.`
- `ACancellationDuringASecondSameStationAppendIsCarriedThroughWithAFreshEngineEachRound`: red,
  `A worklist sent after the cancellation went back to revision 1; the vehicle held 5.`
- also red: `AStaleDepartureAtAStagingStopIsRefusedEvenWithoutASubstantiveChange` (existing).

Before the rework both new cases stayed green under the same probe (review M5).

### Question 3: engine instance-state guard

`JourneyRuntimeEngineInstanceStateArchitectureTests` on `d49fa7143` (field still present) is red and names exactly
`stagingPickupStops (HashSet`1)`; captured primary-constructor parameters and the allow-listed fields pass.

### M2: the staging-stop set is replaced at each recognition (review M2, option B)

`7e36fdc1a`: `stagingPickupStops` is replaced whole at each recognition instead of `UnionWith`, and allow-listed in the
guard with that reason. Review's group (Staging*, *CargoHolding*, *LoadCancellation*, *StationYield*, *EnRouteAppend*,
*ArchitectureTests*): 553/553.

Mutation M6 (`mutations/M6-accumulate-again.diff`: back to `UnionWith`), `--no-incremental`, same group: **553/553 green,
nothing red.** Expected: with the cs#904 fix the recognition counts removed memberships, so it only grows and the
accumulated set equals the replaced one; the guard checks field names, not how a field is written. The accumulating
write is harmless in production (a new engine every round); its only harm is hiding a production defect in tests that
reuse one engine, which nothing here detects any more.
