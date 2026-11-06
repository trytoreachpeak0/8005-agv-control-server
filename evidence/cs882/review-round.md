# cs#882 review round (M1, S3)

Review of c82f2ce1a: no H; M1, S3 fixed here, S1 and S2 written into the PR body.

## Red before the fix (tests 4b209f11a on product c82f2ce1a)

M1 gateway: AnExecuteVehicleKeyThatIsNotReadSaysNothingAboutTheBinding, 6/6 red, all at the binding assert:
```
  失败 ControlServer.Tests.HttpRiotMovementGatewayTests.AnExecuteVehicleKeyThatIsNotReadSaysNothingAboutTheBinding(orderState: 1, shape: "missing") [4 ms]
  失败 ControlServer.Tests.HttpRiotMovementGatewayTests.AnExecuteVehicleKeyThatIsNotReadSaysNothingAboutTheBinding(orderState: 2, shape: "number") [< 1 ms]
  失败 ControlServer.Tests.HttpRiotMovementGatewayTests.AnExecuteVehicleKeyThatIsNotReadSaysNothingAboutTheBinding(orderState: 2, shape: "missing") [< 1 ms]
  失败 ControlServer.Tests.HttpRiotMovementGatewayTests.AnExecuteVehicleKeyThatIsNotReadSaysNothingAboutTheBinding(orderState: 2, shape: "null") [< 1 ms]
  失败 ControlServer.Tests.HttpRiotMovementGatewayTests.AnExecuteVehicleKeyThatIsNotReadSaysNothingAboutTheBinding(orderState: 1, shape: "null") [< 1 ms]
  失败 ControlServer.Tests.HttpRiotMovementGatewayTests.AnExecuteVehicleKeyThatIsNotReadSaysNothingAboutTheBinding(orderState: 1, shape: "number") [< 1 ms]
失败!  - 失败:     6，通过:    11，已跳过:     0，总计:    17，持续时间: 3 s - ControlServer.Tests.dll (net8.0)
   Assert.Null() Failure: Value of type 'Nullable<bool>' has a value  (Expected: null, Actual: False)
```

S3: the new assertions pass on c82f2ce1a and go red under the reviewer's mutations (one product file each, restored with git checkout):

- MI (drop the 2286 LogChargingEndedUndispatched call):
```
  失败 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending: "cancelled", vehicle: "offline") [179 ms]
  失败 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending: "deleted", vehicle: "motion-unknown") [116 ms]
  失败 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending: "cancelled", vehicle: "motion-unknown") [108 ms]
失败!  - 失败:     3，通过:     4，已跳过:     0，总计:     7，持续时间: 3 s - ControlServer.Tests.dll (net8.0)
```
- ME (ORDER_ENDED_WITHOUT_ARRIVAL no longer SeverityHigh):
```
  失败 ControlServer.Tests.ChargingDashboardTests.AChargerReservedByAJourneyWhoseOrderEndedInRiotIsShownOverduePastTheWindow [2 s]
失败!  - 失败:     1，通过:     6，已跳过:     0，总计:     7，持续时间: 3 s - ControlServer.Tests.dll (net8.0)
```

## Green after the fix

Filter (every touched class plus ArchitectureTests): ~Charging|~ArchitectureTests|~MultiVehicleExecutionTests|~Dashboard|~StationClearance|~IdleReturn|~HttpRiot|~Riot
```
已通过! - 失败:     0，通过:  1742，已跳过:     0，总计:  1742，持续时间: 46 s - ControlServer.Tests.dll (net8.0)
```

dotnet format --verify-no-changes on the five files changed this round: exit 0.
