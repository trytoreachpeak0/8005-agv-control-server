# cs#852 review rework (S1, L1-L3, P1-P3)

- Tests: f3e85b84e. Its run (rework-red.txt) is NOT a valid red: the S1 case's heartbeats reused the release's
  session-request and action message ids and were answered as resends. Fixed in 9cf8ed451.
- Implementation: 7313bdd14.
- Red for S1 is taken by mutation on 7313bdd14 (mutations/S1*.diff, summary-s1.txt): S1b (the wait is never marked)
  is the pre-S1 behaviour; S1a/S1b fail at line 364 (no second request after 5 s), S1c at line 379 (still asking after
  the snapshot), S1d at line 369 (no overdue warning). All four on AReleaseWaitingForReaskReadingsAsksAgainUntilTheVehicleAnswers.
- Green: rework-green.txt, 620 tests (RecoveryStateMachineG2Tests, ArchitectureTests, OnboardMessageProcessorTests,
  RecoveryGrant, RefusedSafetyBaselineTests, RemainingBasketsSkip, Retired, Evidence).
- dotnet format --verify-no-changes on the four touched files: exit 0 (rework-format-verify.txt).
