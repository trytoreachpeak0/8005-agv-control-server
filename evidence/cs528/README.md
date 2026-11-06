# control-server#528 evidence

- `red/red-before-fix.txt`: the new tests on the test commit `8dc98325a` (compile stubs only): 6 failed, 29 passed.
- `green/green-after-fix.txt`: the same filter on the fix commit `77c74b574`: 35 passed.
- `green/affected-classes.txt`: every touched or dependent test class run whole (including `ArchitectureTests`,
  `StructuralDispatchBlockTests`, the charging dashboard, execution and cycle classes, idle return,
  `MultiVehicleExecutionTests`, fixed-station exclusivity): 1306 passed.
- `mutation/SUMMARY.txt` and `mutation/M*.txt`: one mutation per file, each restored afterwards; each file opens with the
  diff that was applied. M1–M8 revert a part of the fix; M9 reads charger occupancy through `KnownStationId` (what mapping
  0 to null at the gateway would do) and M10 counts a vehicle at 0 as having left its charger (#801).
