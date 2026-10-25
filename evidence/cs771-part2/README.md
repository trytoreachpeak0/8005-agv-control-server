# control-server#771 part 2 evidence

- `red/` -- the test commits run on stubs: `01` online service and Host entry (17 of 26 red), `02` FieldOps `--server` (4 of 12), `03` dashboard hint (4 of 4).
- `green/01` -- the two blocked-journey dashboard pins gain only `manualWithdrawalHint`; with it removed they equal `fp/v2-impl@3e62d65b9` byte for byte.
- `green/02` -- targeted and adjacent classes on the implementation: 1064 of 1064.
- `mutations/` -- ten point mutations of the new code, each run against its own test class and reverted; each turns at least one test red.
