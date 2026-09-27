# M2-timeout-sixty-seconds
file: src/ControlServer.Infrastructure/Persistence/SessionLiveness.cs
base: 2ded1b3814487f7b39dcef05f7beecc12fd7a791
```diff
diff --git a/src/ControlServer.Infrastructure/Persistence/SessionLiveness.cs b/src/ControlServer.Infrastructure/Persistence/SessionLiveness.cs
index 6f72eb50..f6b0629b 100644
--- a/src/ControlServer.Infrastructure/Persistence/SessionLiveness.cs
+++ b/src/ControlServer.Infrastructure/Persistence/SessionLiveness.cs
@@ -45,7 +45,7 @@ namespace ControlServer.Infrastructure.Persistence;
 public static class SessionLiveness
 {
     /// <summary>ADR-cross-0027：心跳两秒一次，六秒存活超时。</summary>
-    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);
+    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
 
     /// <summary>此刻听得到当前这一代会话的车。</summary>
     public static async Task<HashSet<string>> HeardFromAsync(
```
    0 Error(s)
exit=1
