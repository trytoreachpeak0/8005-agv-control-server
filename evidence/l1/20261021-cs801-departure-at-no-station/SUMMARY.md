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
