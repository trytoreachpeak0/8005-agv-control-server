# cs#791 mutations (each on VehicleFaultCoordinator.cs alone, restored after; filter VehicleFaultIsolationTests|VehicleFaultRecoveryTests)

## M1_no_reread  (reread < faultOptions.ResumeConfirmationRereads && -> reread < 0 &&)
Failed!  - Failed:     6, Passed:   174, Skipped:     0, Total:   180, Duration: 38 s - ControlServer.Tests.dll (net8.0)
Failed ControlServer.Tests.VehicleFaultIsolationTests.AContinueStillHeldWhenTheBudgetRunsOutIsAwaitedAndClearsNothing [8 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AnOrderThatEndsOtherwiseAfterTheContinueIsAFailedContinue(endedAs: 4) [6 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AnOrderThatEndsOtherwiseAfterTheContinueIsAFailedContinue(endedAs: 2) [5 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AContinueStillReadAsHeldStraightAfterTheCallIsReadAgainAndConfirmed(stateAfterContinue: 3) [5 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AContinueStillReadAsHeldStraightAfterTheCallIsReadAgainAndConfirmed(stateAfterContinue: 5) [5 ms]
Failed ControlServer.Tests.VehicleFaultRecoveryTests.AContinueThatFinishesTheOrderAMomentAfterTheReadIsConfirmedAndTheJourneyGoesOn [57 ms]

## M2_no_settle  (bool continued = await ContinueConfirmedAsync( -> bool continued = false && await ContinueConfirmedAsync()
Failed!  - Failed:     5, Passed:   175, Skipped:     0, Total:   180, Duration: 37 s - ControlServer.Tests.dll (net8.0)
Failed ControlServer.Tests.VehicleFaultIsolationTests.SettlingAnEarlierContinueIsRefusedUnderALatchLikeAnyResumption [5 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AskingAgainAfterTheAwaitedContinueTookEffectSettlesItWithoutASecondContinue [83 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AResumptionCutShortAfterTheContinueIsFinishedWhenAskedAgain(written: Confirmed) [4 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AResumptionCutShortAfterTheContinueIsFinishedWhenAskedAgain(written: Pending) [4 ms]
Failed ControlServer.Tests.VehicleFaultRecoveryTests.AContinueAwaitedOnTheFirstRequestIsSettledByTheNextWithoutASecondContinue [135 ms]

## M3_any_generation  ([.. attempts.Where(attempt => attempt.FaultGeneration == faultGeneration)] -> [.. attempts])
Failed!  - Failed:     1, Passed:   179, Skipped:     0, Total:   180, Duration: 37 s - ControlServer.Tests.dll (net8.0)
Failed ControlServer.Tests.VehicleFaultIsolationTests.AContinueConfirmedUnderAnEarlierFaultDoesNotResumeThisOne [5 ms]

## M4_still_need_held  (if (!continueConfirmed && observation.OrderState -> if (observation.OrderState)
Failed!  - Failed:     5, Passed:   175, Skipped:     0, Total:   180, Duration: 37 s - ControlServer.Tests.dll (net8.0)
Failed ControlServer.Tests.VehicleFaultIsolationTests.SettlingAnEarlierContinueIsRefusedUnderALatchLikeAnyResumption [6 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AskingAgainAfterTheAwaitedContinueTookEffectSettlesItWithoutASecondContinue [99 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AResumptionCutShortAfterTheContinueIsFinishedWhenAskedAgain(written: Confirmed) [6 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AResumptionCutShortAfterTheContinueIsFinishedWhenAskedAgain(written: Pending) [5 ms]
Failed ControlServer.Tests.VehicleFaultRecoveryTests.AContinueAwaitedOnTheFirstRequestIsSettledByTheNextWithoutASecondContinue [133 ms]

## M5_failed_as_await  (if (outcome == RiotOrderCommandOutcome.Failed) -> if (false))




(rerun with a runtime-false condition; the constant false failed the build with CS0162)
## M5_failed_as_await  (if (outcome == RiotOrderCommandOutcome.Failed) -> if (outcome == RiotOrderCommandOutcome.Failed && subject.AgvId.Length < 0))
Failed!  - Failed:     2, Passed:   178, Skipped:     0, Total:   180, Duration: 40 s - ControlServer.Tests.dll (net8.0)
Failed ControlServer.Tests.VehicleFaultIsolationTests.AnOrderThatEndsOtherwiseAfterTheContinueIsAFailedContinue(endedAs: 4) [29 ms]
Failed ControlServer.Tests.VehicleFaultIsolationTests.AnOrderThatEndsOtherwiseAfterTheContinueIsAFailedContinue(endedAs: 2) [18 ms]

