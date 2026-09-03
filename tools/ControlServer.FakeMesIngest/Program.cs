using ControlServer.FakeMesIngest;

WebApplication? app = FakeMesIngestHost.TryCreate(args);
if (app is null)
{
    return 2;
}
await app.RunAsync().ConfigureAwait(false);
return 0;
