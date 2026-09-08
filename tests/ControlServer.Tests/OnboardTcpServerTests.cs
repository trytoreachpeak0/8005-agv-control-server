using System.Net;
using System.Net.Sockets;
using ControlServer.Host.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

public sealed class OnboardTcpServerTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task NonLoopbackListenerStartsAndAcceptsPlaintextConnections()
    {
        int port = ReserveFreePort();
        OnboardTransportOptions options = new()
        {
            Enabled = true,
            ListenAddress = "0.0.0.0",
            Port = port
        };
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using OnboardTcpServer server = new(
            Options.Create(options),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OnboardPeer(),
            NullLogger<OnboardTcpServer>.Instance);

        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            Assert.True(client.Connected);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static int ReserveFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}
