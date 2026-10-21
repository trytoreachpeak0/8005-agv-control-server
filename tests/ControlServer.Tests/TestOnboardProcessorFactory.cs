using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        ILogger<OnboardRecoveryCoordinator>? recoveryLogger = null,
        ControlServer.Host.Runtime.Charging.ManualStationClearance? stationClearance = null,
        ControlServer.Host.Runtime.Charging.UnableToChargeFieldConfirmations? fieldConfirmations = null)
    {
        OnboardJourneyPublisher publisher = new(store, peer ?? new SilentPeer(), timeProvider);
        SlotConfigurationActivationDispatcher activationDispatcher = ActivationDispatcher(context, publisher);
        OnboardRecoveryCoordinator coordinator = new(
            context, store, publisher, activationDispatcher, timeProvider, configuration, recoveryLogger,
            runtimeOptions: runtimeOptions is null ? null : Options.Create(runtimeOptions));
        return new OnboardMessageProcessor(
            context,
            store,
            coordinator,
            new OnboardAlarmProjectionStore(context),
            activationDispatcher,
            timeProvider,
            configuration,
            Options.Create(runtimeOptions ?? new JourneyRuntimeOptions()),
            logger ?? NullLogger<OnboardMessageProcessor>.Instance,
            stationClearance: stationClearance,
            fieldConfirmations: fieldConfirmations);
    }

    /// <summary>
    /// Registers the processor the way the host does (<c>AddDbContext</c>, <c>AddScoped</c> in Program.cs): one per scope, so
    /// one per connection the listener serves, each over a context of its own on a connection of its own to
    /// <paramref name="connectionString"/> (control-server#836).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One processor handed to every scope shares one context between the listener's connections. On a reconnect the old
    /// connection's loop is still finishing a line when the new one's <c>SessionHello</c> arrives; EF refuses the second
    /// operation, and the listener ends the new connection with 1003 (CI run 39545289891). Production never shares, so a
    /// test whose rig did saw a failure production cannot have.
    /// </para>
    /// <para>
    /// The database has to be one a second connection reaches: a named shared-cache in-memory one, kept alive by a
    /// connection the caller holds open, or a file. SQLite serialises the connections itself.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddScopedProcessor(
        this IServiceCollection services,
        string connectionString,
        TimeProvider timeProvider,
        IConfiguration configuration,
        IOnboardPeer? peer = null)
    {
        services.AddScoped(_ => new ControlServerDbContext(
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connectionString).Options));
        services.AddScoped(provider =>
        {
            ControlServerDbContext context = provider.GetRequiredService<ControlServerDbContext>();
            return Create(context, new WireToGateStore(context), timeProvider, configuration, peer);
        });
        return services;
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
