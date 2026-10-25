using ControlServer.Application;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Withdrawal;

/// <summary>人工撤下的服务端在线写法（control-server#771 第二部分）。</summary>
public sealed class JourneyWithdrawalService(
    ControlServerDbContext dbContext,
    IGovernanceAuditWriter audit,
    IRiotVehicleOrderFacts orderFacts,
    JourneyMutationGate gate,
    OnboardJourneyPublisher publisher,
    RecoveryGrantRegistry grants,
    TimeProvider timeProvider,
    TimeSpan? gateTimeout = null)
{
    public Task<JourneyWithdrawalResult> PlanAsync(JourneyWithdrawalRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException(
            $"{dbContext}{audit}{orderFacts}{gate}{publisher}{grants}{timeProvider}{gateTimeout}");

    public Task<JourneyWithdrawalResult> WithdrawAsync(JourneyWithdrawalRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
