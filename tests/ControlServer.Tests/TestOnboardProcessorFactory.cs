using ControlServer.Application;
using ControlServer.Domain;
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
        // 治理那一串是 #9 立的地基，激活协调器要它写快照与审计。测试里用默认保留策略与一个
        // 明确标注「不可归因到自然人」的部署身份，与 Host 起服务时同一条路。
        GovernanceStore governance = new(
            context, GovernanceDeploymentIdentity.ForCurrentHost(), AuditRetentionPolicy.Default);
        SlotConfigurationActivationDispatcher activationDispatcher = new(
            context,
            new SlotConfigurationActivationCoordinator(
                context, new GovernedConfigurationPublisher(governance, governance), governance),
            publisher);
        OnboardRecoveryCoordinator coordinator = new(
            context, store, publisher, activationDispatcher, timeProvider, configuration);
        return new OnboardMessageProcessor(
            store,
            coordinator,
            new OnboardAlarmProjectionStore(context),
            activationDispatcher,
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
