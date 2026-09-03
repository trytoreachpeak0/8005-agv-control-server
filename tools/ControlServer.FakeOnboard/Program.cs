using ControlServer.FakeOnboard;

WebApplication? app = FakeOnboardHost.TryCreate(args);
if (app is null)
{
    return 2;
}
await app.StartAsync().ConfigureAwait(false);
await using OnboardPeerSession peer = await FakeOnboardHost
    .ConnectAsync(app, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
await app.WaitForShutdownAsync().ConfigureAwait(false);
return 0;
