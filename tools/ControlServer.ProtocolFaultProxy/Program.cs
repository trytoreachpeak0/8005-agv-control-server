using ControlServer.ProtocolFaultProxy;

WebApplication? app = ProtocolFaultProxyHost.TryCreate(args);
if (app is null)
{
    return 2;
}
await app.RunAsync().ConfigureAwait(false);
return 0;
