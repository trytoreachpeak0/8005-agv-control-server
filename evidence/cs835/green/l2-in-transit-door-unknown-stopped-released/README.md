# cs#835 synthetic L2 in-transit-door-unknown-stopped-released x5 at c114b9568

All five PASS. Per run: emergency stops requested (expected 1), fault rounds logged between the continue's RIoT command and the recovery's answer (the cs#797 race needs at least 1), the recovery's HTTP time, HELD_ORDER_RESUMED_WITHOUT_CONTINUE lines:

```
door-unknown-c114b9568-run1 PASS estop=1 roundsInWindow=0 responded 200 in 583.0966 ms named=0
door-unknown-c114b9568-run2 PASS estop=1 roundsInWindow=0 responded 200 in 557.2758 ms named=0
door-unknown-c114b9568-run3 PASS estop=1 roundsInWindow=0 responded 200 in 652.1492 ms named=0
door-unknown-c114b9568-run4 PASS estop=1 roundsInWindow=0 responded 200 in 562.1804 ms named=0
door-unknown-c114b9568-run5 PASS estop=1 roundsInWindow=0 responded 200 in 557.0092 ms named=0
```

No run put an engine round inside the continue's window: every continue settled in about 0.6 s. The race itself is covered by
the L1 AnEngineRoundInsideTheContinuesReadBackDoesNotStopTheVehicleItResumed (red on the base, green on the fix). For comparison, the
coordinator's local runs on cs#797's head 33668e5c went red 1 in 3 when the continue took about 1 s.
