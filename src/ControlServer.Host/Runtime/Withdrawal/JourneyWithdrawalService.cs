using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Withdrawal;

/// <summary>
/// 人工撤下的服务端在线写法（control-server#771 第二部分）：服务不停、闸不动，判定与写入仍是第一部分那一段
/// （<see cref="JourneyWithdrawal"/>），这里补上在线才有的三件事。
/// </summary>
/// <remarks>
/// <para>
/// <b>RIoT 由服务端自己读</b>：这辆车在 RIoT 上有没有未结束的单，读数进计划、进确认码（读数时刻不进），读不全或有单都拒。
/// 离线写法只能信人填的 <c>--riot-verification</c>，在线不再退回去信它。
/// </para>
/// <para>
/// <b>与运行轮次串行</b>，与故障人工恢复同一个做法（<see cref="Faults.VehicleFaultRecoveryService"/>、<see cref="JourneyMutationGate"/>）：
/// RIoT 在拿闸之前读，拿到闸之后撤下核心在一个写事务里重读本库、重判、写；持闸期间不调 RIoT，也不往车上发东西。轮次在
/// <see cref="DefaultGateTimeout"/> 内不结束就拒（<see cref="JourneyWithdrawal.RuntimeBusy"/>），一行不写。演练只读，不拿闸。
/// 派车轮不在闸里：它在撤下之前读到这趟旅程可以追加、撤下之后才提交的那一次，由追加自己在事务里重判旅程阶段挡住
/// （<c>WireToGateStore.StageAndCommitAppendAsync</c>），不写任何东西。
/// </para>
/// <para>
/// <b>放闸之后</b>：收尾快照当场发给在线的车（离线写法留在发件箱等重连补发）；这辆车的恢复授权若点名的正是这一趟，一并收回、写一条
/// 收回审计（调度 10-25 定）。授权点名别的旅程，或者是按车的（不点名旅程），不动：它们说的不是这一趟。
/// </para>
/// <para>
/// 撤下本身不查恢复授权：ADR-cross-0066 把在线撤下划在授权之外（它撤的是旅程，不开恢复会话）。车上开着恢复会话时照旧拒。
/// </para>
/// </remarks>
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
    /// <summary>等运行轮次放闸的上限，与故障人工恢复相同：一轮通常远短于轮询间隔，三十秒是卡住的一轮，不是忙。</summary>
    public static readonly TimeSpan DefaultGateTimeout = Faults.VehicleFaultRecoveryService.DefaultGateTimeout;

    /// <summary>随撤下一并收回授权时，收回审计里写的理由。</summary>
    public const string GrantRevokedWithJourneyReason = "JOURNEY_WITHDRAWN_BY_OPERATOR";

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly TimeSpan _gateWait = gateTimeout ?? DefaultGateTimeout;

    /// <summary>演练：读一次 RIoT，判一次，一行不写、不写审计、不拿闸。</summary>
    public async Task<JourneyWithdrawalResult> PlanAsync(JourneyWithdrawalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RiotVehicleOrderObservation orders = await ReadOrdersAsync(request, cancellationToken).ConfigureAwait(false);
        return await JourneyWithdrawal.PlanAsync(dbContext, request, orders, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>撤下：闸外读 RIoT，闸内判与写，放闸之后发收尾快照、收回点名这一趟的授权。</summary>
    public async Task<JourneyWithdrawalResult> WithdrawAsync(JourneyWithdrawalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RiotVehicleOrderObservation orders = await ReadOrdersAsync(request, cancellationToken).ConfigureAwait(false);

        JourneyWithdrawalResult result;
        using (IDisposable? round = await gate.TryEnterAsync(_gateWait, cancellationToken).ConfigureAwait(false))
        {
            if (round is null)
            {
                return new JourneyWithdrawalResult(
                    "REJECTED", [JourneyWithdrawal.RuntimeBusy], null, null, null, RiotObservedAt: orders.ObservedAt);
            }
            result = await JourneyWithdrawal.WithdrawAsync(
                dbContext, audit, request, timeProvider.GetUtcNow(), publisher: null, orders, cancellationToken).ConfigureAwait(false);
        }

        if (result is not { Outcome: "WITHDRAWN", Plan: { } plan })
        {
            return result;
        }
        await JourneyClosure.SendAsync(publisher, dbContext, plan.AgvId, cancellationToken).ConfigureAwait(false);
        RecoveryGrant? revoked = grants.RevokeIfJourney(plan.AgvId, plan.JourneyId);
        if (revoked is null)
        {
            return result;
        }
        await audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                RecoveryGrantIssuance.RevokeAuditAction,
                GovernedObjectKind.RecoveryGrant,
                "vehicle:" + revoked.VehicleKey,
                null,
                GovernanceActionOutcome.Succeeded,
                JsonSerializer.Serialize(
                    new
                    {
                        said = new
                        {
                            vehicleKey = revoked.VehicleKey,
                            operatorId = request.OperatorId?.Trim(),
                            reason = GrantRevokedWithJourneyReason,
                            journeyId = plan.JourneyId,
                            withdrawalAuditRecordId = result.AuditRecordId
                        },
                        revoked,
                        result = "REVOKED"
                    },
                    DetailOptions),
                ClaimedAdministratorRole: request.ClaimedRole?.Trim()),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return result with { RevokedGrantId = revoked.GrantId };
    }

    /// <summary>
    /// RIoT 上这辆车未结束的单。没点名车（请求本身会因此被拒）或读的时候出错，都当作读不全：拒，而不是当作没有单。
    /// </summary>
    private async Task<RiotVehicleOrderObservation> ReadOrdersAsync(JourneyWithdrawalRequest request, CancellationToken cancellationToken)
    {
        string? vehicleKey = string.IsNullOrWhiteSpace(request.VehicleKey) ? null : request.VehicleKey.Trim();
        if (vehicleKey is null)
        {
            return Unknown(string.Empty);
        }
        try
        {
            return await orderFacts.ReadUnfinishedOrdersAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return Unknown(vehicleKey);
        }
    }

    private RiotVehicleOrderObservation Unknown(string vehicleKey) =>
        new(vehicleKey, HasUnfinishedOrder: null, [], timeProvider.GetUtcNow());
}
