# M3-idle-close-keeps-route
file: src/ControlServer.Host/Transport/OnboardTcpServer.cs
base: 57f39f3339c712451cdfb517ce4911a0e2687943
```diff
diff --git a/src/ControlServer.Host/Transport/OnboardTcpServer.cs b/src/ControlServer.Host/Transport/OnboardTcpServer.cs
index 668e1548..2aa2551f 100644
--- a/src/ControlServer.Host/Transport/OnboardTcpServer.cs
+++ b/src/ControlServer.Host/Transport/OnboardTcpServer.cs
@@ -237,7 +237,7 @@ public sealed partial class OnboardTcpServer : BackgroundService
         }
         finally
         {
-            if (attachedAgvId is not null)
+            if (attachedAgvId is not null && !liveness.Expired)
             {
                 _peer.Detach(attachedAgvId, connection);
             }
```
    0 Error(s)
exit=1
