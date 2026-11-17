# control-server#897 evidence

Door fault escalated (evidence rewritten to `RIOT_EMERGENCY_CAN_NOT_RECOVER`), its held order
then cancelled in RIoT: the rebuild waited for the clearance and the clearance refused the
cancelled order (agv03, 2026-11-15).

## Red before the fix

`red-before-fix.log`: commit `dcd9c89c0` (the column and its writing in place, the clearance
still judging `EvidenceCode`), with the first three tests of `DoorFaultEscalatedThenCancelledInRiotTests`:

| Test | Result | Why |
| --- | --- | --- |
| field sequence (then a `[Fact]`, now the `after` row) | red at the clearance | `FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT` -- the field deadlock |
| `AFaultThatNeverWasADoorFaultStillRefuses...` | green | the pre-fix rule refuses it too; it pins the #335 review M1 rule against widening |
| `ADoorFaultRaisedOnAnotherOrderDoesNotClear...` | red | the pre-fix rule let a door fault raised on another order clear over this one |

The tests added afterwards at the coordinator's request were taken red through M1 below, which is
the pre-fix rule: both escalation orders (`before`, `after`) and the pre-column row.

## Mutations

Each applied to one file alone (`mutations/M*.diff`), the listed test classes run, the file restored.
M1-M3 on `5240e2ad3` with `DoorFaultEscalatedThenCancelledInRiotTests`, `EmergencyReleaseVersusOwnOrderRebuildTests`,
`FaultedCargoBindingLifecycleTests` (30 tests); M1 again and M4-M6 on `3f6958ab2` with `Batch2CapabilityStoresTests` added (50 tests).

| Mutation | File | What it does | Red |
| --- | --- | --- | --- |
| M1 | `VehicleFaultRecoveryService.cs` | judge on `EvidenceCode` only (the pre-fix rule) | field sequence `before` and `after`; the pre-column row's way out |
| M2 | `VehicleFaultRecoveryService.cs` | drop the hold-on-this-order check | the door fault raised on another order |
| M3 | `VehicleFaultRecoveryService.cs` | drop the origin check | the never-a-door-fault case, and #349's `ConstructedALatchOverALiveOwnOrder...` |
| M4 | `Batch2CapabilityStores.cs` | overwrite the origin on every evaluation | `TheOriginSymptomIsKeptThroughAnEscalationAndRenewedByTheNextGeneration` |
| M5 | `Batch2CapabilityStores.cs` | a new generation keeps the previous origin (`??=`) | the store test, and `ASecondFaultOnTheSameVehicleDoesNotInheritTheDoorOriginOfTheFirst` |
| M6 | migration | `Down` leaves the column | `TheMigrationAddsOneNullableColumnAndGoesDownAndUpWithoutTouchingAFaultRow` |

M4 is not caught by the engine-driven tests: under today's callers a door fault escalates while being
re-observed on the door symptom, so the clearance sees the same origin either way. The store test
pins the rule directly.
