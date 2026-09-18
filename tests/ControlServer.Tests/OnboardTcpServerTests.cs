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
    [Trait("IntegrationSlice", "W2G-IS-00")]
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

    /// <summary>
    /// Issue #148: a connection reset while it waits in the listen backlog makes the accept throw. That
    /// must cost one connection, not the listener -- and through StopHost, not the whole service.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task AcceptFailureForResetBacklogConnectionKeepsListenerRunning()
    {
        int port = ReserveFreePort();
        OnboardTransportOptions options = new()
        {
            Enabled = true,
            ListenAddress = "127.0.0.1",
            Port = port
        };
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        int attempts = 0;
        TaskCompletionSource secondAccept = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using OnboardTcpServer server = new(
            Options.Create(options),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OnboardPeer(),
            NullLogger<OnboardTcpServer>.Instance)
        {
            Accept = async (listener, cancellationToken) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    // What Windows reports for a backlog connection its peer reset before accept.
                    throw new SocketException((int)SocketError.ConnectionReset);
                }
                TcpClient accepted = await listener.AcceptTcpClientAsync(cancellationToken);
                secondAccept.TrySetResult();
                return accepted;
            }
        };

        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            await secondAccept.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.NotNull(server.ExecuteTask);
            Assert.False(server.ExecuteTask.IsCompleted, "The listener loop ended after one failed accept.");
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
