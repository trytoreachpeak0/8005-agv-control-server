using ControlServer.FakeRiot;

WebApplication? app = FakeRiotHost.TryCreate(args);
if (app is null)
{
    return 2;
}
await app.RunAsync().ConfigureAwait(false);
return 0;
