# control-server#771 part 2 evidence

- `red/` -- the test commits run on stubs: `01` online service and Host entry (17 of 26 red), `02` FieldOps `--server` (4 of 12), `03` dashboard hint (4 of 4).
- `green/01` -- the two blocked-journey dashboard pins gain only `manualWithdrawalHint`; with it removed they equal `fp/v2-impl@3e62d65b9` byte for byte.
- `green/02` -- targeted and adjacent classes on the implementation: 1064 of 1064.
- `mutations/` -- ten point mutations of the new code, each run against its own test class and reverted; each turns at least one test red.

## Option 2 (the user's choice on 2026-10-25): the server cancels this journey's own running orders first

- `red/04` -- the option 2 tests on the stub that still refused every listed order: 7 of 33 red, each on `RIOT_ORDER_NOT_FINISHED`.
- `green/03` -- after merging `fp/v2-impl@c3a45bc68 (review L5: earlier written as 03b3f0254 by mistake)` and implementing option 2: targeted and adjacent classes (architecture and text guards included) 940 of 940. The withdrawal classes alone are 93 of 93 after the last test was added.
- `mutations/M11`-`M17` -- seven point mutations of the option 2 code, one file each, runtime-false conditions, reverted and checked clean after each; every build succeeded, every one turns at least one test red:
  M11 own order never cancelled, M12 the service does not refuse an unconfirmed cancellation, M13 a foreign order taken for this journey's,
  M14 criteria not judged again after the cancellation, M15 the engine's block code kept in the post-cancellation digest,
  M16 the confirmation code not checked before cancelling, M17 RIoT not read again after the cancellation.
  M12 survived the first round (the core's second step refused the same way, the order still being listed); the test
  `ACancellationWhoseReadBackIsUnknownWithdrawsNothingEvenWhenTheOrderIsNoLongerListed` was added for the case only the service sees, and the M12 file is that re-run.

## Review r5006 rework (Coordinator14, 2026-11-11)

- `red/05` -- the review tests on the test commit `06cfbe490` (implementation still `3c18acd41`): 7 of 170 red, exactly the behaviour changes
  (own order past the pickup leg, audit when busy after a cancel, run to the end without the client, replay with RIoT unreadable,
  FieldOps TIMED_OUT, hint for ORDER_ENDED_WITHOUT_ARRIVAL, no hint for the faulted row in the pin). The probes P1, P2, P6 turned tests are
  green there by design: they guard MR2 and MR3.
- `red/R01`-`R09` -- one mutation per must-fix item on the fixed code `793176919`, one file each, runtime-false conditions, reverted and
  checked clean after each, every build succeeded; each turns its new test red: R01 M1 (cancel on any leg), R02 the reviewer's MR2,
  R03 MR3, R04 S3, R05 S5, R06 L1, R07 S2, R08 S4, R09 L6.
- `green/04` -- targeted and adjacent classes after a clean rebuild: 950 of 950. `dotnet format --verify-no-changes` on the changed files exit 0.
- The pin `dashboard-blocked-journeys-own-move-order.txt` changed by one value only: the D-FAULTED row's `manualWithdrawalHint` is `null`.
