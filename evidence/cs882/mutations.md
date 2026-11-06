# cs#882 mutations

Base: 8eba87fce33fe7206e521d810e918fea67ba787e. One product file per mutation, restored with git checkout of that file only; worktree verified clean after each.
Filter: FullyQualifiedName~ChargingOrderEndedBeforeArrivalTests|~AChargerReservedByAJourneyWhoseOrderEndedInRiot|~ChargingApproachSupervisionTests (18 tests)

| Id | Mutation | Result |
|---|---|---|
| M1 | engine: undispatched branch never taken (`ExecuteVehicleBound == false && OrderState == Success`) | red, 5 failed |
| M2 | engine: `!= true` (unread binding taken as undispatched) | red, 1 failed (ACancelWhoseBindingWasNotRead…) |
| M3 | engine: undispatched ending does not release the charger | red, 5 failed |
| M4 | dashboard: ORDER_ENDED_WITHOUT_ARRIVAL flagged before the window | red, 1 failed (dashboard overdue) |
| M5 | engine: 2256 names the journey in place of the cycle | red, 1 failed |
| M6 | reasons: undispatched ending not in ConfirmedFailures | red, 1 failed (cooldown / two strikes) |

```
M1 exit=1 失败!  - 失败:     5，通过:    13，已跳过:     0，总计:    18，持续时间: 4 s - ControlServer.Tests.dll (net8.0) 
    ControlServer.Tests.ChargingApproachSupervisionTests.OnceAPersonCancelsTheUndispatchedOrderInRiotTheChargerIsFreedAtOnceAndNothingIsRebuilt
    ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending:
    ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AnUndispatchedEndingCountsAsAConfirmedFailureCoolsTheVehicleDownAndTwiceStopsAutomaticCharging
M2 exit=1 失败!  - 失败:     1，通过:    17，已跳过:     0，总计:    18，持续时间: 4 s - ControlServer.Tests.dll (net8.0) 
    ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.ACancelWhoseBindingWasNotReadIsNotTakenAsUndispatched
M3 exit=1 失败!  - 失败:     5，通过:    13，已跳过:     0，总计:    18，持续时间: 3 s - ControlServer.Tests.dll (net8.0) 
    ControlServer.Tests.ChargingApproachSupervisionTests.OnceAPersonCancelsTheUndispatchedOrderInRiotTheChargerIsFreedAtOnceAndNothingIsRebuilt
    ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending:
    ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AnUndispatchedEndingCountsAsAConfirmedFailureCoolsTheVehicleDownAndTwiceStopsAutomaticCharging
M4 exit=1 失败!  - 失败:     1，通过:    17，已跳过:     0，总计:    18，持续时间: 4 s - ControlServer.Tests.dll (net8.0) 
    ControlServer.Tests.ChargingDashboardTests.AChargerReservedByAJourneyWhoseOrderEndedInRiotIsShownOverduePastTheWindow
M5 exit=1 失败!  - 失败:     1，通过:    17，已跳过:     0，总计:    18，持续时间: 4 s - ControlServer.Tests.dll (net8.0) 
    ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedAfterRiotBoundTheVehicleKeepsTheChargerAndAlarmsWithVehicleChargerAndCyclePastTheWindow
M6 exit=1 失败!  - 失败:     1，通过:    17，已跳过:     0，总计:    18，持续时间: 4 s - ControlServer.Tests.dll (net8.0) 
    ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AnUndispatchedEndingCountsAsAConfirmedFailureCoolsTheVehicleDownAndTwiceStopsAutomaticCharging
```
