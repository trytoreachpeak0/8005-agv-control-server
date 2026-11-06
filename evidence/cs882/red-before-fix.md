# cs#882 red run (before the fix)

Base: 0f7b3afa008059720949280eb02cb1a55b21a50a (fp/v2-impl, contains cs#879 0f7b3afa0)
Command: dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --filter "FullyQualifiedName~ChargingOrderEndedBeforeArrivalTests|FullyQualifiedName~AChargerReservedByAJourneyWhoseOrderEndedInRiot"

```
  失败 ControlServer.Tests.ChargingDashboardTests.AChargerReservedByAJourneyWhoseOrderEndedInRiotIsShownOverduePastTheWindow [2 s]
  失败 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedAfterRiotBoundTheVehicleKeepsTheChargerAndAlarmsWithVehicleChargerAndCyclePastTheWindow [2 s]
  失败 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending: "cancelled", vehicle: "offline") [125 ms]
  失败 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending: "deleted", vehicle: "motion-unknown") [114 ms]
  失败 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.AChargeOrderEndedBeforeRiotEverBoundAVehicleEndsTheCycleAndFreesTheChargerAtOnce(ending: "cancelled", vehicle: "motion-unknown") [110 ms]
  已通过 ControlServer.Tests.ChargingOrderEndedBeforeArrivalTests.ACancelWhoseBindingWasNotReadIsNotTakenAsUndispatched [87 ms]
   Assert.True() Failure
Expected: True
Actual:   False
   Assert.Contains() Failure: Sub-string not found
String:    "Charging journey charging:BROKERX-0001:20260908T06"···
   Assert.Equal() Failure: Values differ
Expected: Tuple (Completed, "CHARGING_ORDER_ENDED_UNDISPATCHED")
Actual:   Tuple (AwaitingPickupArrival, "ORDER_ENDED_WITHOUT_ARRIVAL")
   Assert.Equal() Failure: Values differ
Expected: Tuple (Completed, "CHARGING_ORDER_ENDED_UNDISPATCHED")
Actual:   Tuple (AwaitingPickupArrival, "ORDER_ENDED_WITHOUT_ARRIVAL")
   Assert.Equal() Failure: Values differ
Expected: Tuple (Completed, "CHARGING_ORDER_ENDED_UNDISPATCHED")
Actual:   Tuple (AwaitingPickupArrival, "ORDER_ENDED_WITHOUT_ARRIVAL")
```
