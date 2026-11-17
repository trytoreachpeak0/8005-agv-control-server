# control-server#897 evidence

Door fault escalated (evidence rewritten to `RIOT_EMERGENCY_CAN_NOT_RECOVER`), its held order
then cancelled in RIoT: the rebuild waited for the clearance and the clearance refused the
cancelled order (agv03, 2026-11-15).

## Red before the fix

`red-before-fix.log`: commit `dcd9c89c0` (the column and its writing in place, the clearance
still judging `EvidenceCode`). `DoorFaultEscalatedThenCancelledInRiotTests`:

| Test | Result | Why |
| --- | --- | --- |
| `ADoorFaultEscalatedByAHeldButtonClears...TheOneRebuildRuns` | red at the clearance | `FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT` -- the field deadlock |
| `AFaultThatNeverWasADoorFaultStillRefuses...` | green | the pre-fix rule refuses it too; it pins the #335 review M1 rule against widening |
| `ADoorFaultRaisedOnAnotherOrderDoesNotClear...` | red | the pre-fix rule let a door fault raised on another order clear over this one (only `FAULT_RECOVERY_EMERGENCY_STOP_OPEN` was named) |

## Mutations (on the fix, `5240e2ad3`)

Each applied to `VehicleFaultRecoveryService.cs` alone (`mutations/M*.diff`), run with
`DoorFaultEscalatedThenCancelledInRiotTests`, `EmergencyReleaseVersusOwnOrderRebuildTests` and
`FaultedCargoBindingLifecycleTests` (30 tests), then the file restored.

| Mutation | What it does | Red |
| --- | --- | --- |
| M1 | judge on `EvidenceCode` only (the pre-fix rule) | the field sequence |
| M2 | drop the hold-on-this-order check | the door fault raised on another order |
| M3 | drop the origin check (any fault that held the order) | the never-a-door-fault case, and #349's constructed `ConstructedALatchOverALiveOwnOrder...` |

Not mutated: writing `OriginSymptomCode` on every evaluation instead of only at a new generation.
Under today's callers it is equivalent at the clearance (a door fault is re-observed with the door
symptom while it escalates); `Batch2CapabilityStoresTests.TheOriginSymptomIsKeptThroughAnEscalationAndRenewedByTheNextGeneration`
pins the store's rule directly.
