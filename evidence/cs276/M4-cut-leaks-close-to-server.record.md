# M4-cut-leaks-close-to-server
file: tests/ControlServer.Tests/OnboardPowerLossReconnectTests.cs
base (test file = this commit before the mutation; product = 57f39f3339c712451cdfb517ce4911a0e2687943)
```diff
--- a/tests/ControlServer.Tests/OnboardPowerLossReconnectTests.cs
+++ b/tests/ControlServer.Tests/OnboardPowerLossReconnectTests.cs
@@ -663,6 +663,7 @@
                         if (Cut)
                         {
                             // Power is gone: nothing more reaches the server, and its side is never closed.
+                            server.Dispose();
                             return;
                         }
                         if (read == 0)
```
    0 Error(s)
exit=1
