# cs#866 evidence

## Why 10-23 raised no 2163/2164

Read-only, from `C:/work/8005-scratch/power-loss-20261023` (production build `9c8c34a5`) and the code at `fp/v2-impl@03b3f025`.

- RIoT snapshots (`01-snaps-1200-now.txt`, local time): agv01 `EXECUTING/MT_WAIT_FOR_CHECKPOINT` with order
  `order-2113584881723375616` from 18:59:04 to 21:31:12, battery 61% -> 31%; 21:35 `PAUSE/MT_RUNNING` 27%,
  21:40 11%, 21:50 `conn=false`.
- agv01 onboard log (`12-agv01.log`): the session was `RecoveryRequired / [DEPARTURE_UNSAFE]` from 18:54:39 through
  21:39:30 without a Ready in between (reconnect at 21:15 came back RecoveryRequired too). That is the designed state of
  a real onboard while this server's order is in flight.
- Engine (`JourneyRuntimeEngine.AdvanceAsync`, readiness gate): with no Ready session the round names an in-flight
  order problem (`NameInFlightOrderWithoutThePeerAsync`: HANG, FAILED, cancelled) or else writes
  `ONBOARD_SESSION_NOT_READY` and returns. The checkpoint check (`NameCheckpointWaitAsync`) sits in the arrival branches
  after the gate, so it never ran. An executing order is none of the stalled states, so the journey read
  `AwaitingGateArrival / ONBOARD_SESSION_NOT_READY` for the whole wait.
- `JourneyWaitClassification.IsWaiting` deliberately does not count `ONBOARD_SESSION_NOT_READY` on a leg (cs#320), so
  `WaitingJourneyWatch` skipped the journey: no battery reading recorded, no 2163/2164.
- So: the reason code was not written (not "under budget"). The journey row itself is not in the evidence folder;
  the conclusion rests on the onboard log plus the code path, and the red test reproduces it on the engine.

## 11-08 (issue comment)

The watch did fire: 2164 every 5 minutes from 23:41 at `AwaitingUnloadResult`, as Error with the "below the mandatory
charge entry threshold" advice (findings file). Nothing was missed by the watch; what was missing is that it was only a
log line. The new dashboard alarm covers that case (`ALoadedVehicleWaitingAtItsUnload...`). The departure at 39% is not
this ticket.

## Files

- `red/new-tests-on-test-commit.txt` - the five new tests on the test commit (product code unchanged): all fail for the
  reason the ticket names.
- `green/` - new tests and the targeted run on the implementation.
- `red/mutation-*.txt` - one mutation per file, runtime condition, restored after each.

## Mutations (on `07481ded8`, one file each, runtime condition, restored after)

Filter: the five new tests.

| Mutation | What it breaks | Result |
| --- | --- | --- |
| M1 | the gate never asks about a checkpoint | 3 red |
| M2 | no 30 s ration behind the gate | 1 red (`Expected: 2 / Actual: 4` samples) |
| M3 | 2105 logged every round past budget | 1 red (`Assert.Single`, 3 items) |
| M4 | idle-return and charging journeys alarmed | 1 red |
| M5 | journeys not waiting alarmed | 2 red |
| M6 | battery at or above the line alarmed | 2 red |
| M7 | alarm not registered HIGH | 1 red (`MEDIUM`) |

## Review rework (review of `252644e08`, report `C:/work/8005-scratch/review-cs866/SUMMARY.md`)

- Tests first: `6baf53069`; `red/review-tests-on-reviewed-head.txt` - eight red on the reviewed implementation, the
  two-vehicle S1 test green by design (a guard, proven by mutation R-S1).
- The D-UNREAD rows of `OnlyAWaitingJourney...` use the new `VehicleBatteryReadings` API, so they came with the
  implementation commit; mutation R-M3 shows they and the engine-level M3 test go red without the fallback.
- Battery source for the driving alarm (S5): the dispatch round's `ReadVehicleAsync` for every admitted vehicle, which
  already ran before the en-route plan check; recorded in memory on `ChargingAllocationBoard.Batteries`. No RIoT request
  and no database write were added to either round.

### Rework mutations (on `61e22987c`, one file each, runtime condition, restored after; filter: every cs#866 test)

| Mutation | Review item | Result |
| --- | --- | --- |
| R-M1 | checkpoint code not taken as explained by the own order | 2 red (pickup plan test; S3 test, whose cut is then never reached) |
| R-M2 | an unread sample taken as "not waiting" | 2 red (`OneFailedCheckpointReadDoesNotRestartTheWait`, both sides of the gate) |
| R-M3 | no fallback to the last reading when the watch reads nothing | 2 red (engine M3 test, D-UNREAD) |
| R-S1 | gate ration keyed for the whole fleet | 1 red (two-vehicle test) |
| R-S2 | no sample budget | 1 red (round took 20 s) |
| R-S3 | transport failure guard ignores checkpoint codes | 1 red (`JOURNEY_ADVANCE_FAILED`) |
| R-S4 | endpoint not isolated | 1 red |
| R-S5 | driving journeys not reported | 1 red |
| R-S5b | dispatch round does not record its reading | 1 red |
| R-L3 | since not the waiting start | 3 red |

`green/targeted-review-rework.txt`: 1591 targeted green on the committed head after a no-incremental build.
