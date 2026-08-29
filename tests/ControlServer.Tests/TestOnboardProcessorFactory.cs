using ControlServer.Application;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

internal static class TestOnboardProcessorFactory
{
    public static OnboardMessageProcessor Create(
        ControlServerDbContext context,
        WireToGateStore store,
        TimeProvider timeProvider,
        IConfiguration configuration,
        IOnboardPeer? peer = null)
    {
        OnboardJourneyPublisher publisher = new(store, peer ?? new SilentPeer(), timeProvider);
        OnboardRecoveryCoordinator coordinator = new(
            context, store, publisher, timeProvider, configuration);
        return new OnboardMessageProcessor(
            store,
            coordinator,
            timeProvider,
            configuration,
            NullLogger<OnboardMessageProcessor>.Instance);
    }

    private sealed class SilentPeer : IOnboardPeer
    {
        public Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            _ = ndjsonLine;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
