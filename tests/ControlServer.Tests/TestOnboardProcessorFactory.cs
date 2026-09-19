using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

internal static class TestOnboardProcessorFactory
{
    public static OnboardMessageProcessor Create(
        ControlServerDbContext context,
        WireToGateStore store,
        TimeProvider timeProvider,
        IConfiguration configuration,
        IOnboardPeer? peer = null,
        JourneyRuntimeOptions? runtimeOptions = null,
        ILogger<OnboardMessageProcessor>? logger = null,
        ILogger<OnboardRecoveryCoordinator>? recoveryLogger = null)
    {
        OnboardJourneyPublisher publisher = new(store, peer ?? new SilentPeer(), timeProvider);
        SlotConfigurationActivationDispatcher activationDispatcher = ActivationDispatcher(context, publisher);
        OnboardRecoveryCoordinator coordinator = new(
            context, store, publisher, activationDispatcher, timeProvider, configuration, recoveryLogger);
        return new OnboardMessageProcessor(
            context,
            store,
            coordinator,
            new OnboardAlarmProjectionStore(context),
            activationDispatcher,
            timeProvider,
            configuration,
            Options.Create(runtimeOptions ?? new JourneyRuntimeOptions()),
            logger ?? NullLogger<OnboardMessageProcessor>.Instance);
    }

    /// <summary>
    /// The same coordinator the processor above is built with, for a test that needs to reach one of its
    /// entry points directly rather than through an inbound line.
    /// </summary>
    public static OnboardRecoveryCoordinator CreateRecoveryCoordinator(
        ControlServerDbContext context,
        WireToGateStore store,
        TimeProvider timeProvider,
        IConfiguration configuration,
        IOnboardPeer? peer = null)
    {
        OnboardJourneyPublisher publisher = new(store, peer ?? new SilentPeer(), timeProvider);
        return new OnboardRecoveryCoordinator(
            context,
            store,
            publisher,
            ActivationDispatcher(context, publisher),
            timeProvider,
            configuration);
    }

    private static SlotConfigurationActivationDispatcher ActivationDispatcher(
        ControlServerDbContext context,
        OnboardJourneyPublisher publisher)
    {
        // 治理那一串是 #9 立的地基，激活协调器要它写快照与审计。测试里用默认保留策略与一个
        // 明确标注「不可归因到自然人」的部署身份，与 Host 起服务时同一条路。
        GovernanceStore governance = new(
            context, GovernanceDeploymentIdentity.ForCurrentHost(), AuditRetentionPolicy.Default);
        return new SlotConfigurationActivationDispatcher(
            context,
            new SlotConfigurationActivationCoordinator(
                context, new GovernedConfigurationPublisher(governance, governance), governance),
            publisher,
            governance);
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
