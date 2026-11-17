# control-server#900 evidence

A door fault escalated by a held button (evidence rewritten to `RIOT_EMERGENCY_CAN_NOT_RECOVER`) was no
longer recognised by the in-transit door supervision, the ended-order door supervision or the stop's
door-cause fallback, which compared the current `EvidenceCode` only. Fix: judge by
`OriginSymptomCode ?? EvidenceCode` (column from #899), as the clearance does.

## Red before the fix

`red-before-fix.log`: commit `4f0d784fb` (tests only, production code as on `fp/v2-impl` `a08270d04`),
`EscalatedDoorFaultSupervisionTests`, 14 tests: 7 red, 7 green.

| Test (row) | Before the fix | Why |
| --- | --- | --- |
| `AnEscalatedDoorFaultHeldWithItsDoorsReadLockedStillStopsTheVehicleAgain` (`after`/`before`, `runs-without-continue`) | red: no second stop | doors read locked -> `BeforeFault` returns `Nothing`; nothing evaluates the fault after the person's release |
| same (`after`/`before`, `external-release`) | green | the open stop is watched every round by `WatchOpenStopAsync` (cs#539 review H-1), independent of the door path; kept as a guard |
| `AnEscalatedDoorFaultIsStillSupervisedAfterItsOrderIsCancelledInRiot` (`after`, `before`) | red: `LastEvaluatedAt` frozen | `SuperviseDoorFaultOnEndedOrderAsync` returned early (the 11-15 agv03 symptom) |
| `AGenerationRecordedWithoutItsHoldIsHeldOnTheNextRound` (`running`, `cancelled`) | red: no hold under the generation | cs#899 review L1 crash window |
| `AFaultThatNeverWasADoorFaultIsNotPickedUpByTheDoorSupervision` (`running`, `cancelled`) | green | today's behaviour, pinned against widening |
| `TheStopsDoorCauseFallbackReadsTheFaultsOrigin` (`escalated-door`) | red: false | fallback compared `EvidenceCode` only |
| same (`never-door`, `legacy-door`, `legacy-other`) | green | today's behaviour |

## After the fix

On `1c66a388f`, targeted local run (`EscalatedDoorFaultSupervisionTests`, `DoorFaultEscalatedThenCancelledInRiotTests`,
`InTransitDoor*`, `LoadedEmergencyStopRelease*`, `EmergencyStop*`, `VehicleFault*`, `EmergencyReleaseVersusOwnOrderRebuild*`,
`OwnOrderRebuild*`, `StoppedRebuild*`, `FailedOrder*`, `FaultedCargo*`, `DoorLatch*`, `*ArchitectureTests*`,
`Batch2CapabilityStoresTests`): 833 passed, 0 failed.

## Mutations

Each applied to one file alone (`mutations/M*.diff`) on `1c66a388f`, then `EscalatedDoorFaultSupervisionTests`,
`DoorFaultEscalatedThenCancelledInRiotTests`, `InTransitDoor*`, `LoadedEmergencyStopRelease*` run (198 tests), the file
restored. Failing tests per mutation in `mutations/M*.result.txt`.

| Mutation | File | What it does | Red |
| --- | --- | --- | --- |
| M1 | `JourneyRuntimeEngine.InTransitDoors.cs` | `DoorFaultInEffectAsync` judges on `EvidenceCode` only (the old rule) | 6: both `runs-without-continue`, both cancelled-order rows, both crash-window rows |
| M2 | same | old rule at `ObserveDoorsInTransitAsync` only | 3: both `runs-without-continue`, crash window `running` |
| M3 | same | old rule at `SuperviseDoorFaultOnEndedOrderAsync` only | 3: both cancelled-order rows, crash window `cancelled` |
| M4 | `LoadedEmergencyStopRelease.cs` | door-cause fallback on `EvidenceCode` only | `escalated-door` |
| M5 | same | door-cause fallback on `OriginSymptomCode` only (no fallback for a pre-column row) | `legacy-door` |
| M6 | `JourneyRuntimeEngine.InTransitDoors.cs` | any standing fault counts as a door fault | both never-a-door-fault rows |
