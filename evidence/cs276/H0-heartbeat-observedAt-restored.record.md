# H0-heartbeat-observedAt-restored
file: tools/ControlServer.FakeOnboard/OnboardPeerSession.cs
base: 2ded1b3814487f7b39dcef05f7beecc12fd7a791
```diff
diff --git a/tools/ControlServer.FakeOnboard/OnboardPeerSession.cs b/tools/ControlServer.FakeOnboard/OnboardPeerSession.cs
index c865931e..fb27e076 100644
--- a/tools/ControlServer.FakeOnboard/OnboardPeerSession.cs
+++ b/tools/ControlServer.FakeOnboard/OnboardPeerSession.cs
@@ -220,6 +220,9 @@ public sealed class OnboardPeerSession(
             while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
             {
                 FakeOnboardState state = engine.Snapshot().State;
+                // Exactly the two fields Heartbeat.schema.json allows. An observedAt here was refused by the
+                // outbound schema gate (additionalProperties) the first time a test kept this peer alive past
+                // its first beat (control-server#276); the server never read it.
                 await SendLineAsync(Envelope("Heartbeat", NewId(), null, state.SessionGeneration, new
                 {
                     observedAt = DateTimeOffset.UtcNow,
```
    0 Error(s)
exit=1
