# cs#801 evidence index

Ticket: trytoreachpeak0/8005-agv-control-server#801. Departure evidence now needs the vehicle read standing at
another station (`CurrentStationId > 0 && != held station`); `currentPosition` 0 is RIoT's "no station".

| Commit | What |
| --- | --- |
| `339e17e2` | L1 tests (charger x2 shapes + guard, waiting point, public station) |
| `32fd06d7` | tests name the released holder instead of dereferencing it |
| `dc8aeea9` | fix: `ChargingAllocator.WhyNotYetReleasable`, `FixedStationExclusivitySweep.HasDepartedAsync` |
| `6812b5a6` | guard test runs with two vehicles (the other vehicle was not in the fleet) |
| `8468a206` | formatting |
| `8fa0fa9a` | synthetic L2 `charging-full-cycle` gets L2-CFC-06 |

## red/

- `01-tests-at-32fd06d70.txt`: the 5 new tests on the test commit (fix not yet in): 4 failed, the guard passed.
- `02-mutation-M1-charger-branch-off.txt`: only the charger branch removed: the 2 charger cases fail.
- `03-mutation-M2-sweep-accepts-zero.txt`: sweep takes `>= 0`: waiting point and public station fail.
- `04-mutation-M3-every-zero-unknown.txt`: over-fix, every vehicle at 0 makes chargers unknown (the rule cs#772
  rejected): the guard `AnotherVehicleDrivingAtNoStationDoesNotKeepALeftChargerFromBeingReleased` fails.

Each mutation changed exactly one file and was restored with `git checkout -- <file>`.

## green/

- `01-tests-at-dc8aeea9d.txt`: the 5 new tests with the fix: 5/5.
- `02-affected-classes-at-dc8aeea9d.txt`: 20 related classes (charging, idle return, fixed station, manual release,
  multi-vehicle, clearance, waiting point, architecture): 1078/1078.
- `03-tests-at-6812b5a66.txt`: the 5 new tests after the guard fix: 5/5.
- `04-format-at-8fa0fa9a9.txt`: `dotnet format --verify-no-changes` on the changed files. The two remaining
  `IdleReturnExecutionTests.cs` lines 185 and 1388 are from `b3a8be72` (2026-09-30), not this ticket.

## l2-green-8fa0fa9a9/, l2-red-M1-8fa0fa9a9/

Synthetic `charging-full-cycle` (`SUMMARY.md`, `assertions.json` only). Green: all six criteria PASS. Red (M1 applied
to the product): L2-CFC-06 FAIL, expected `0 / NO_CHARGE / OCCUPIED … | COMPLETE`, actual `0 / NO_CHARGE / (none) | NOT_CHARGING`.

## Review rework (independent review of `72ce1f04`: M1, M2)

| Commit | What |
| --- | --- |
| `d5cf2718` | L1: M1 (warned on the charger, then stuck at no station: warned again) and M2 (failed cycle at no station) |
| `6b431c35` | fix M1: the at-no-station case has its own say-once key, timed from the first reading at 0 |

- `red/05-review-tests-at-d5cf27188.txt`: M1 test fails (`Expected: 2, Actual: 1` warnings 2249); the M2 test passes:
  it pins existing behaviour (reservation kept at 0, own verdict `CHARGING_VEHICLE_STILL_HOLDS_CHARGER`, 2247 says
  "at no station", released once at 12).
- `green/05-review-tests-at-6b431c359.txt`: the 7 cs#801 tests: 7/7.
- `green/06-affected-classes-at-6b431c359.txt`: 21 related classes (now also the charging dashboard): 1112/1112.
- `red/06-mutation-M4-shared-key.txt`: at-no-station uses the old key again: M1 test fails (1 warning).
- `red/07-mutation-M5-clock-from-completion.txt`: at-no-station timed from the completion: M1 test fails at the
  "no warning in the first five minutes after leaving" step.
- `green/07-format-at-6b431c359.txt`: same two pre-existing `IdleReturnExecutionTests.cs` lines only.
