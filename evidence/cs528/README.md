# control-server#528 evidence

- `red/red-before-fix.txt`: the new tests on the test commit `8dc98325a` (compile stubs only): 6 failed, 29 passed.
- `green/green-after-fix.txt`: the same filter on the fix commit `77c74b574`: 35 passed.
- `green/affected-classes.txt`: every touched or dependent test class run whole (including `ArchitectureTests`,
  `StructuralDispatchBlockTests`, the charging dashboard, execution and cycle classes, idle return,
  `MultiVehicleExecutionTests`, fixed-station exclusivity): 1306 passed.
- `mutation/SUMMARY.txt` and `mutation/M*.txt`: one mutation per file, each restored afterwards; each file opens with the
  diff that was applied. M1–M8 revert a part of the fix; M9 reads charger occupancy through `KnownStationId` (what mapping
  0 to null at the gateway would do) and M10 counts a vehicle at 0 as having left its charger (#801).
- Review rework (`b95a82cf3`, review S1–S3):
  - `mutation/review-S1-M7-en-route.txt`: M7 again, now caught by
    `MultiVehicleExecutionTests.AVehicleUnderWayThatRiotReadsAtNoStationSitsOutTheRoundAndTakesNoAppend` (the vehicle is back
    among the round's participants).
  - `mutation/review-S2-2246-other-reason.txt`: the position-unknown 2246 logged under another reason; the reason-slot
    assertion catches it (the template's own hints no longer can pass it).
  - `mutation/review-S3-withdrawal-keeps-charger.txt`: a position-unknown withdrawal that keeps the charger reserved; caught
    by the new "no holder" assertion, after every assertion the test had before.
  - `green/after-review-classes.txt`: the touched classes run whole with `ArchitectureTests`: 393 passed.
  The product code under these mutations is `39e0ef2b` plus comments and one Chinese description (L4).
