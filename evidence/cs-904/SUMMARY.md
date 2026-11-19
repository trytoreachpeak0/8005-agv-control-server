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
