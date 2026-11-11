using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
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
    IRiotVehicleFacts vehicleFacts,
    RiotOrderCommandService orderCommands,
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

    /// <summary>
    /// 撤下之前取消这一趟自己的单时，写进订单命令审计的理由。引擎认得这一类审计行（<c>RiotOrderCommandAudit</c> 上这张单的
    /// <c>CANCEL_ORDER</c>），看见单没了不重建（<c>JourneyRuntimeEngine.OwnOrderRebuild</c>，事件 2174）。
    /// </summary>
    public const string CancellationReason =
        "control-server#771 manual withdrawal: this journey's own order is cancelled before the journey is withdrawn";

    /// <summary>
    /// 演练：读一次 RIoT（这辆车未结束的单与车此刻的位置、速度），判一次，一行不写、不写审计、不拿闸、不取消任何单。
    /// 这一趟自己的单还在跑时，计划把它列成 <see cref="JourneyWithdrawal.CancelBeforeWithdrawal"/>，确认码覆盖它的单号。
    /// </summary>
    public async Task<JourneyWithdrawalResult> PlanAsync(JourneyWithdrawalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        JourneyWithdrawalOnlineFacts online = await ReadOnlineFactsAsync(request, cancellations: null, cancellationToken)
            .ConfigureAwait(false);
        return await JourneyWithdrawal.PlanAsync(dbContext, request, online, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 撤下（用户 10-25 选方案二）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>第一步</b>：闸外读 RIoT 与车，闸内判、核确认码。这一趟自己的单不在 RIoT 上跑，当场撤下，与之前一样。
    /// </para>
    /// <para>
    /// <b>这一趟自己的单还在跑</b>：第一步什么都不写，放闸；闸外经订单命令面逐张取消（每张先武装审计行再发、读回对账，与
    /// <c>DemandReleaseService</c> 释放前的取消同一套）。有一张没有对账成 <see cref="RiotOrderCommandOutcome.Confirmed"/> 就不撤：
    /// 写一条失败审计（<see cref="JourneyWithdrawal.OrderCancelNotConfirmed"/>，列出每次取消的结果），旅程与需求一行不改，交人到 RIoT
    /// 看；人重新演练、再执行，会再发一次取消（新的一次尝试，不覆盖旧的）。都确认了：再读一次 RIoT，再拿闸，撤下核心再判每一条准入，
    /// 拿第一步算的摘要核「人看过的就是这一份」（取消本身带来的变化不算），通过才写。
    /// </para>
    /// <para>
    /// 不是这一趟的单（<see cref="JourneyWithdrawal.NotThisJourneys"/>）一律不碰，撤下拒（<see cref="JourneyWithdrawal.RiotOrderNotFinished"/>）。
    /// 确认码对不上时在发任何取消之前就拒。
    /// </para>
    /// <para>
    /// 取消让车停在当时的位置，可能停在机台单行通道里；演练输出车的位置与速度，生产上每次先问用户（调度 10-25 定 Q4）。
    /// </para>
    /// </remarks>
    public async Task<JourneyWithdrawalResult> WithdrawAsync(JourneyWithdrawalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        JourneyWithdrawalOnlineFacts first = await ReadOnlineFactsAsync(request, cancellations: null, cancellationToken)
            .ConfigureAwait(false);
        JourneyWithdrawalResult? held = await WithdrawUnderTheGateAsync(
            request, first, JourneyWithdrawalBinding.ByCodeHoldingForCancellation, cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            return Busy(first);
        }
        if (held.Outcome != JourneyWithdrawal.CancellationRequired)
        {
            return await AfterTheGateAsync(request, held, cancellationToken).ConfigureAwait(false);
        }

        JourneyWithdrawalPlan plan = held.Plan!;
        JourneyWithdrawalRiotOrder[] own =
        [
            .. plan.RiotOrders!.Orders.Where(order => order.Disposition == JourneyWithdrawal.CancelBeforeWithdrawal)
        ];
        List<JourneyWithdrawalOrderCancellation> cancellations = [];
        foreach (JourneyWithdrawalRiotOrder order in own)
        {
            RiotOrderCommandRecord record = await orderCommands.IssueAsync(
                RiotOrderCommandKind.Cancel,
                new RiotOrderCommandTarget(plan.AgvId, order.UpperId!, order.OrderId),
                CancellationReason,
                faultGeneration: null,
                cancellationToken).ConfigureAwait(false);
            cancellations.Add(new JourneyWithdrawalOrderCancellation(order.OrderId, order.UpperId!, record.Outcome.ToString()));
        }

        if (cancellations.Any(item => item.Outcome != nameof(RiotOrderCommandOutcome.Confirmed)))
        {
            return await JourneyWithdrawal.RefuseAfterCancellationAsync(
                audit, request, plan, [JourneyWithdrawal.OrderCancelNotConfirmed], timeProvider.GetUtcNow(),
                first with { Cancellations = cancellations }, cancellationToken).ConfigureAwait(false);
        }

        JourneyWithdrawalOnlineFacts second = await ReadOnlineFactsAsync(request, cancellations, cancellationToken)
            .ConfigureAwait(false);
        JourneyWithdrawalResult? withdrawn = await WithdrawUnderTheGateAsync(
            request,
            second,
            JourneyWithdrawalBinding.AfterCancellation(held.CancellationDigest!, [.. own.Select(order => order.UpperId!)]),
            cancellationToken).ConfigureAwait(false);
        return withdrawn is null
            ? Busy(second)
            : await AfterTheGateAsync(request, withdrawn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拿闸、判与写；轮次在等待上限内不放闸时为空（一行不写）。持闸期间不调 RIoT、不往车上发。</summary>
    private async Task<JourneyWithdrawalResult?> WithdrawUnderTheGateAsync(
        JourneyWithdrawalRequest request,
        JourneyWithdrawalOnlineFacts online,
        JourneyWithdrawalBinding binding,
        CancellationToken cancellationToken)
    {
        using IDisposable? round = await gate.TryEnterAsync(_gateWait, cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            return null;
        }
        return await JourneyWithdrawal.WithdrawAsync(
                dbContext, audit, request, timeProvider.GetUtcNow(), publisher: null, online, binding, cancellationToken)
            .ConfigureAwait(false);
    }

    private static JourneyWithdrawalResult Busy(JourneyWithdrawalOnlineFacts online) =>
        new("REJECTED", [JourneyWithdrawal.RuntimeBusy], null, null, null)
        {
            RiotObservedAt = online.Orders.ObservedAt,
            Vehicle = online.Vehicle,
            Cancellations = online.Cancellations
        };

    /// <summary>放闸之后：撤下了就当场发收尾快照，并收回点名这一趟的恢复授权、写收回审计。</summary>
    private async Task<JourneyWithdrawalResult> AfterTheGateAsync(
        JourneyWithdrawalRequest request,
        JourneyWithdrawalResult result,
        CancellationToken cancellationToken)
    {
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

    /// <summary>这一次读到的 RIoT：这辆车未结束的单与车此刻的位置、速度，加上已经发过的取消。</summary>
    private async Task<JourneyWithdrawalOnlineFacts> ReadOnlineFactsAsync(
        JourneyWithdrawalRequest request,
        IReadOnlyList<JourneyWithdrawalOrderCancellation>? cancellations,
        CancellationToken cancellationToken)
    {
        string? vehicleKey = string.IsNullOrWhiteSpace(request.VehicleKey) ? null : request.VehicleKey.Trim();
        RiotVehicleOrderObservation orders = await ReadOrdersAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        JourneyWithdrawalVehicle? vehicle = await ReadVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        return new JourneyWithdrawalOnlineFacts(orders, vehicle, cancellations);
    }

    /// <summary>
    /// RIoT 上这辆车未结束的单。没点名车（请求本身会因此被拒）或读的时候出错，都当作读不全：拒，而不是当作没有单。
    /// </summary>
    private async Task<RiotVehicleOrderObservation> ReadOrdersAsync(string? vehicleKey, CancellationToken cancellationToken)
    {
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

    /// <summary>车此刻在哪、动不动，只给人看（不进确认码、不作判据）：读不到就不给，不因此拒。</summary>
    private async Task<JourneyWithdrawalVehicle?> ReadVehicleAsync(string? vehicleKey, CancellationToken cancellationToken)
    {
        if (vehicleKey is null)
        {
            return null;
        }
        try
        {
            RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(vehicleKey, cancellationToken)
                .ConfigureAwait(false);
            return new JourneyWithdrawalVehicle(
                vehicle.Connected, vehicle.CurrentMap, vehicle.CurrentStationId, vehicle.Speed, vehicle.ProcState,
                vehicle.ObservedAt);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private RiotVehicleOrderObservation Unknown(string vehicleKey) =>
        new(vehicleKey, HasUnfinishedOrder: null, [], timeProvider.GetUtcNow());
}
