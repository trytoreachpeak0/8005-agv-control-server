# M3-idle-close-keeps-route
file: src/ControlServer.Host/Transport/OnboardTcpServer.cs
base: 2ded1b3814487f7b39dcef05f7beecc12fd7a791
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
