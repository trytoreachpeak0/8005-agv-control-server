# M1-window-one-hour
file: src/ControlServer.Host/Transport/OnboardTcpServer.cs
base: 57f39f3339c712451cdfb517ce4911a0e2687943
```diff
diff --git a/src/ControlServer.Host/Transport/OnboardTcpServer.cs b/src/ControlServer.Host/Transport/OnboardTcpServer.cs
index 668e1548..12eee615 100644
--- a/src/ControlServer.Host/Transport/OnboardTcpServer.cs
+++ b/src/ControlServer.Host/Transport/OnboardTcpServer.cs
@@ -21,7 +21,7 @@ public sealed partial class OnboardTcpServer : BackgroundService
         IServiceScopeFactory scopeFactory,
         OnboardPeer peer,
         ILogger<OnboardTcpServer> logger)
-        : this(options, scopeFactory, peer, logger, TimeProvider.System, SessionLiveness.Timeout)
+        : this(options, scopeFactory, peer, logger, TimeProvider.System, TimeSpan.FromHours(1))
     {
     }
 
```
    0 Error(s)
exit=1
