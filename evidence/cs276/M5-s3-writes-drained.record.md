# M5-s3-writes-drained
file: tests/ControlServer.Tests/OnboardPowerLossReconnectTests.cs
base (test file = this commit before the mutation; product = 57f39f3339c712451cdfb517ce4911a0e2687943)
```diff
--- a/tests/ControlServer.Tests/OnboardPowerLossReconnectTests.cs
+++ b/tests/ControlServer.Tests/OnboardPowerLossReconnectTests.cs
@@ -107,7 +107,7 @@
         await using Rig rig = await Rig.StartAsync();
         long oldGeneration = await rig.ConnectFirstSessionAsync();
 
-        rig.Relay.CutPower(drainServerWrites: false);
+        rig.Relay.CutPower(drainServerWrites: true);
         using CancellationTokenSource pushing = new();
         Task pusher = rig.PushToSessionUntilCancelledAsync(oldGeneration, payloadBytes: 64 * 1024, pushing.Token);
         Outcome outcome;
```
    0 Error(s)
exit=1
