# cs#835 mutations

Each mutation changes exactly one file (checked with `git diff --name-only`), runs
`FullyQualifiedName~InTransitDoorEmergencyReleaseTests|~InTransitDoorLockFault|~InTransitDoorReadingRestored`
(115 tests; 117 after the second test commit), and is reverted with `git checkout -- <file>`.
Run on the fix (M1-M4, M6 on c76149187; M5, M7 on 87dda3fd3, after the two tests that kill them).

| Mutation | What it breaks | Killed by |
| --- | --- | --- |
| M1 refused continue counts (`last.Outcome == Failed` check dropped) | a continue RIoT refused explains the motion | `AVehicleThatMovesAfterItsContinueWasRefusedIsStillStoppedAndNamed` |
| M2 order state not checked (`OrderState == Executing` -> true) | an open continue under an order still HELD explains the motion | `AVehicleThatMovesWhileItsOwnContinueIsOpenAndTheOrderStillReadsHeldIsStopped` |
| M3 doors not proven locked exempted (`&& !context.DoorsNotProvenLocked` dropped) | a resumed vehicle whose doors read not locked is not stopped | `AStopTheEngineReissuesWhileTheContinueIsReadBackKeepsTheFaultAndLeavesAWayOut` (cs#791 H1) |
| M4 engine names own continue (`!decision.ResumedByOwnContinue` dropped) | HELD_ORDER_RESUMED_WITHOUT_CONTINUE written for the own continue | `AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed` x3 |
| M5 any generation counts (generation filter dropped) | an earlier fault's confirmed continue vouches for this one | `AContinueOfAnEarlierFaultGenerationIsNotThisOnesOwnContinue`, `TheExemptionEndsWithTheFaultGenerationEvenOnceTheDoorsAreLockedAgain` |
| M6 recognition removed (always false; the base behaviour) | the own continue is stopped again | `AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed` x3, `AnEngineRoundInsideTheContinuesReadBackDoesNotStopTheVehicleItResumed` x2 |
| M7 order identity not checked (orderId comparison dropped) | another order under the same upperId explains the motion | `AnotherOrderRunningUnderTheSameUpperIdIsNotTheOwnContinue` |

M7 survived the first round (no test reached it) and M5's first form did not compile
(CA1826); both were rerun after the second test commit. Raw output: summary lines below.

```
M1-refused-continue-counts (src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs): exit=1 Failed!  - Failed:     1, Passed:   114, Skipped:     0, Total:   115, Duration: 1 m 36 s
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AVehicleThatMovesAfterItsContinueWasRefusedIsStillStoppedAndNamed [2 s]
M2-order-state-not-checked (src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs): exit=1 Failed!  - Failed:     1, Passed:   114, Skipped:     0, Total:   115, Duration: 1 m 36 s
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AVehicleThatMovesWhileItsOwnContinueIsOpenAndTheOrderStillReadsHeldIsStopped [7 s]
M3-doors-not-proven-locked-exempted (src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs): exit=1 Failed!  - Failed:     1, Passed:   114, Skipped:     0, Total:   115, Duration: 1 m 33 s
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AStopTheEngineReissuesWhileTheContinueIsReadBackKeepsTheFaultAndLeavesAWayOut [2 s]
M4-engine-names-own-continue (src/ControlServer.Host/Runtime/JourneyRuntimeEngine.InTransitDoors.cs): exit=1 Failed!  - Failed:     3, Passed:   112, Skipped:     0, Total:   115, Duration: 1 m 31 s
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed(path: "plain") [2 s]
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed(path: "behind-the-readiness-gate") [2 s]
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed(path: "restarted") [2 s]
M6-recognition-removed (src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs): exit=1 Failed!  - Failed:     5, Passed:   110, Skipped:     0, Total:   115, Duration: 1 m 31 s
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed(path: "plain") [2 s]
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed(path: "behind-the-readiness-gate") [2 s]
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnOwnContinueReadHeldFirstAndRunningLaterIsNeitherStoppedNorNamed(path: "restarted") [2 s]
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnEngineRoundInsideTheContinuesReadBackDoesNotStopTheVehicleItResumed(behindTheReadinessGate: False) [2 s]
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnEngineRoundInsideTheContinuesReadBackDoesNotStopTheVehicleItResumed(behindTheReadinessGate: True) [2 s]
M7-order-identity-not-checked (src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs): exit=0 Passed!  - Failed:     0, Passed:   115, Skipped:     0, Total:   115, Duration: 1 m 33 s
M7-order-identity-not-checked (src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs): exit=1 Failed!  - Failed:     1, Passed:   116, Skipped:     0, Total:   117, Duration: 1 m 36 s
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AnotherOrderRunningUnderTheSameUpperIdIsNotTheOwnContinue [2 s]
M5-any-generation-counts (src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs): exit=1 Failed!  - Failed:     2, Passed:   115, Skipped:     0, Total:   117, Duration: 1 m 35 s
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.TheExemptionEndsWithTheFaultGenerationEvenOnceTheDoorsAreLockedAgain [2 s]
      Failed ControlServer.Tests.InTransitDoorEmergencyReleaseTests.AContinueOfAnEarlierFaultGenerationIsNotThisOnesOwnContinue [2 s]
```
