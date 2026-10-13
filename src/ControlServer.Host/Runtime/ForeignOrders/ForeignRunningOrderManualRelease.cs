using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.ForeignOrders;

/// <summary>
/// 一次外来单人工释放的请求（control-server#617）。车与订单号都由人点名，不推断；文本字段可空——缺了是一条拒绝理由，不是格式错误。
/// </summary>
/// <param name="VehicleKey">按车队登记从 <paramref name="AgvId"/> 解出的 RIoT 车号，只进审计。</param>
/// <param name="RiotOrderId">看板「车上的外来订单」那一行的 RIoT 订单号。</param>
/// <param name="SiteVerification">在 RIoT 上核实这张单的记录引用（截图编号、巡检单号等）。</param>
public sealed record ForeignRunningOrderManualReleaseRequest(
    string AgvId,
    string VehicleKey,
    string RiotOrderId,
    string? OperatorId,
    string? Reason,
    string? SiteVerification);

/// <summary>一次判定的结果。</summary>
public sealed record ForeignRunningOrderManualReleaseResult(
    bool Released,
    IReadOnlyList<string> Codes,
    string AuditRecordId);

/// <summary>
/// 外来单人工释放（control-server#617）：一张外来订单离开了 RIoT 的运行列表，按订单号却读不出它已终结
/// （<see cref="ForeignRiotOrderStates.Unsettled"/>），这辆车就一直被按住——监看只认明确终结才放车（REQ-0164）。按单号回读修好之后
/// （同一张票：网页手动派的单没有 <c>upperId</c>，SDK 曾把 RIoT 的有效答复当成失败），这种行仍可能出现：RIoT 里查不到这张单（被删了），
/// 或回查调用一直失败。这里是它的出口：具名、有角色的人说明在 RIoT 上核实过，服务端自己再读一次 RIoT、确认这张单已不在运行，才放车。
/// </summary>
/// <remarks>
/// <para>
/// <b>放宽类入口</b>：与等待点到点人工收尾同一把 Bearer 凭据、同一个开关（<c>VehicleFaultRecovery:enabled</c>），每一次请求——拒绝也算——写一条
/// 管理员审计（<see cref="AuditAction"/>）。谁能办：<see cref="FieldOperatorRoleRoster"/> 里有 R-11 或 R-13 的人（与人工清桩同一份名单、同一组角色）。
/// </para>
/// <para><b>前提，全部要满足</b>（不满足的每一条都列出来）：</para>
/// <list type="number">
/// <item>这辆车有这张单的外来单记录，而且它此刻是 <see cref="ForeignRiotOrderStates.Unsettled"/>。别的挡车状态都表示单还在我们车上运行（或取消还在进行），
/// 那要先在 RIoT 处理；已终结、已放开的不用办。</item>
/// <item>RIoT 现读：未完成订单列表读全了，这张单不在里面——任何未完成状态都不在（排队、执行、暂停、挂起 8、HANG、优先排队）。列表覆盖 RIoT 全部非终态，
/// 所以不在列表里就是已终结或查无此单。</item>
/// <item>RIoT 现读：按订单号回读不是未完成状态。读到明确终结、查无此单或读不到都行：读不到时证明「不在运行」的是上一条的列表，这正是回查坏掉时也要有的兜底；
/// 读到未完成状态（与列表矛盾，例如两次读之间它又回来了）就拒绝。</item>
/// </list>
/// <para>
/// <b>RIoT 不在写锁里读</b>（control-server#452）：先在任何事务之外读库、读 RIoT，再开事务；事务里重读这一行，状态与 <see cref="ForeignRiotOrderRow.UpdatedAt"/>
/// 都没变才写，变了（监看这期间又看见它在跑、或回查读到了终结、或别人刚办过）就拒绝（<see cref="StateChanged"/>），什么也不写。
/// </para>
/// <para>
/// <b>放开之后</b>：行落 <see cref="ForeignRiotOrderStates.ReleasedByOperator"/>，不再挡车。它像已终结的行一样留作记录：RIoT 若再把这张单列出来、跑在我们车上，
/// 监看照常把它重新认出来、重新挡车（<see cref="ForeignRunningOrderSupervisor"/>）。回读读到明确终结时，那个状态记进 <see cref="ForeignRiotOrderRow.EndedOrderState"/>。
/// </para>
/// </remarks>
public sealed class ForeignRunningOrderManualRelease(
    ControlServerDbContext dbContext,
    IRiotOrderListingFacts orders,
    FieldOperatorRoleRoster roles,
    IGovernanceAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<ForeignRunningOrderManualRelease> logger)
{
    /// <summary>每一次请求写的管理员审计动作。</summary>
    public const string AuditAction = "FOREIGN_RUNNING_ORDER_MANUAL_RELEASE";

    public const string OperatorRequired = "FOREIGN_ORDER_RELEASE_OPERATOR_REQUIRED";
    public const string ReasonRequired = "FOREIGN_ORDER_RELEASE_REASON_REQUIRED";
    public const string SiteVerificationRequired = "FOREIGN_ORDER_RELEASE_SITE_VERIFICATION_REQUIRED";
    public const string FieldTooLong = "FOREIGN_ORDER_RELEASE_FIELD_TOO_LONG";
    public const string NotAuthorized = "FOREIGN_ORDER_RELEASE_NOT_AUTHORIZED";
    public const string OrderNotFound = "FOREIGN_ORDER_RELEASE_ORDER_NOT_FOUND";
    public const string NotUnsettled = "FOREIGN_ORDER_RELEASE_NOT_UNSETTLED";
    public const string ListingUnreadable = "FOREIGN_ORDER_RELEASE_LISTING_UNREADABLE";
    public const string OrderStillUnfinished = "FOREIGN_ORDER_RELEASE_ORDER_STILL_UNFINISHED";
    public const string OrderReadNotEnded = "FOREIGN_ORDER_RELEASE_ORDER_READ_NOT_ENDED";
    public const string StateChanged = "FOREIGN_ORDER_RELEASE_STATE_CHANGED";

    /// <summary>理由与核实记录的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>办理人与订单号的最长长度。</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>每一个拒绝码给现场人员看的中文说明：Host 入口的拒绝响应里逐条带上（<c>descriptions</c>）。</summary>
    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OperatorRequired] = "没有写办理人（operatorId）",
        [ReasonRequired] = "没有写理由（reason）",
        [SiteVerificationRequired] = "没有写核实记录（siteVerification：在 RIoT 上查这张单的截图编号、巡检单号等）",
        [FieldTooLong] = "有一项写得太长：理由与核实记录各不超过 500 字，办理人与订单号各不超过 128 字",
        [NotAuthorized] = "办理人不在 R-11／R-13 名单里（或名单没配、读不到）：只有名单里的人能办",
        [OrderNotFound] = "这辆车没有这个订单号的外来订单记录：请照看板「车上的外来订单」里这辆车那一行的订单号填写",
        [NotUnsettled] = "这张外来订单此刻不是「离开运行列表但读不出结束」：它若还在挡车，说明它还在我们车上运行或取消还在进行，请先在 RIoT 结束它；"
                         + "它若已不挡车（已终结、已离开这辆车或已人工放开），不用再办",
        [ListingUnreadable] = "这一刻没能读全 RIoT 的未完成订单列表（RIoT 不应答或分页对不上）：证明不了这张单已不在运行，稍后再办",
        [OrderStillUnfinished] = "RIoT 的未完成订单列表里还有这张单（排队、执行、暂停、挂起或 HANG）：它还没结束，不能放车。请先在 RIoT 把它结束或删除",
        [OrderReadNotEnded] = "按订单号回读，RIoT 报这张单处在未完成状态（与列表对不上，可能两次读之间它又回来了）：不能放车，请到 RIoT 再核实",
        [StateChanged] = "核对期间这张外来订单的记录变了（服务端刚看见它又在跑、刚读到它已终结，或别人刚办过）：什么也没写，请看一眼看板再提交一次",
    };

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly Action<ILogger, string, string, string, string, Exception?> LogReleased =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2233, nameof(LogReleased)),
            "Foreign order {OrderId} on vehicle {AgvId} was released by hand by {OperatorId}: RIoT no longer lists it unfinished, and " +
            "its own read answered {ReadBack}. The vehicle is no longer held for it (control-server#617).");

    public async Task<ForeignRunningOrderManualReleaseResult> DecideAsync(
        ForeignRunningOrderManualReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset now = timeProvider.GetUtcNow();
        string orderId = request.RiotOrderId.Trim();
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? siteVerification = Trimmed(request.SiteVerification);

        List<string> codes = [];
        string? cancelSettledAs = null;
        if (operatorId is null) codes.Add(OperatorRequired);
        if (reason is null) codes.Add(ReasonRequired);
        if (siteVerification is null) codes.Add(SiteVerificationRequired);
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (siteVerification?.Length ?? 0) <= MaxTextLength &&
                    (operatorId?.Length ?? 0) <= MaxIdentifierLength && orderId.Length <= MaxIdentifierLength;
        if (!fits) codes.Add(FieldTooLong);
        string? role = fits ? roles.GrantedRole(operatorId, FieldOperatorRoleRoster.StationClearanceRoles) : null;
        if (operatorId is not null && fits && role is null) codes.Add(NotAuthorized);

        // ---- What the database says, read outside any transaction. ----
        dbContext.ChangeTracker.Clear();
        ForeignRiotOrderRow? read = !fits
            ? null
            : await dbContext.ForeignRiotOrders.AsNoTracking()
                .SingleOrDefaultAsync(row => row.RiotOrderId == orderId && row.AgvId == request.AgvId, cancellationToken)
                .ConfigureAwait(false);
        if (fits && read is null)
        {
            codes.Add(OrderNotFound);
        }
        else if (read is not null && read.State != ForeignRiotOrderStates.Unsettled)
        {
            codes.Add(NotUnsettled);
        }
        if (codes.Count > 0)
        {
            return await RefuseAsync(null).ConfigureAwait(false);
        }

        // ---- What RIoT says, read outside any transaction (control-server#452). ----
        RiotFacts facts = await ReadRiotAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (!facts.ListingComplete)
        {
            codes.Add(ListingUnreadable);
        }
        else if (facts.Listed)
        {
            codes.Add(OrderStillUnfinished);
        }
        if (facts.ReadState is { } readState && !ForeignRunningOrders.IsExplicitlyEnded(readState))
        {
            codes.Add(OrderReadNotEnded);
        }
        if (codes.Count > 0)
        {
            return await RefuseAsync(facts).ConfigureAwait(false);
        }

        // ---- Re-judged under the write lock, against the row read. ----
        string auditRecordId;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            ForeignRiotOrderRow? row = await dbContext.ForeignRiotOrders
                .SingleOrDefaultAsync(item => item.RiotOrderId == orderId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.State != ForeignRiotOrderStates.Unsettled || row.UpdatedAt != read!.UpdatedAt ||
                !string.Equals(row.AgvId, read.AgvId, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                codes.Add(StateChanged);
                return await RefuseAsync(facts).ConfigureAwait(false);
            }

            row.State = ForeignRiotOrderStates.ReleasedByOperator;
            row.EndedOrderState = ForeignRunningOrders.IsExplicitlyEnded(facts.ReadState) ? facts.ReadState : null;
            row.EndedAt = now;
            row.UpdatedAt = now;
            cancelSettledAs = await SettleCancelAsync(row, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            auditRecordId = await WriteAuditAsync(GovernanceActionOutcome.Succeeded, "RELEASED", facts).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        dbContext.ChangeTracker.Clear();

        LogReleased(
            logger, orderId, read!.AgvId, operatorId!,
            facts.ReadState?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NO_STATE", null);
        return new ForeignRunningOrderManualReleaseResult(true, [], auditRecordId);

        async Task<ForeignRunningOrderManualReleaseResult> RefuseAsync(RiotFacts? riot)
        {
            string id = await WriteAuditAsync(GovernanceActionOutcome.Failed, "REJECTED", riot).ConfigureAwait(false);
            return new ForeignRunningOrderManualReleaseResult(false, [.. codes], id);
        }

        Task<string> WriteAuditAsync(GovernanceActionOutcome outcome, string result, RiotFacts? riot) =>
            audit.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    AuditAction,
                    GovernedObjectKind.ForeignRiotOrder,
                    orderId.Length <= MaxIdentifierLength ? orderId : orderId[..MaxIdentifierLength],
                    null,
                    outcome,
                    JsonSerializer.Serialize(
                        new
                        {
                            agvId = request.AgvId,
                            vehicleKey = request.VehicleKey,
                            said = fits
                                ? (object)new { riotOrderId = orderId, operatorId, reason, siteVerification }
                                : new
                                {
                                    riotOrderIdLength = orderId.Length,
                                    operatorIdLength = operatorId?.Length,
                                    reasonLength = reason?.Length,
                                    siteVerificationLength = siteVerification?.Length,
                                },
                            grantedRole = role,
                            row = read is null
                                ? null
                                : new
                                {
                                    read.RiotOrderId,
                                    read.UpperId,
                                    read.AgvId,
                                    read.DeviceKey,
                                    read.Ownership,
                                    read.OwnershipBasis,
                                    read.State,
                                    read.CancelCommandAuditId,
                                    read.DetectedAt,
                                    read.LastSeenRunningAt,
                                    read.UpdatedAt,
                                },
                            riot,
                            cancelSettledAs,
                            codes,
                            result,
                        },
                        DetailOptions),
                    ClaimedAdministratorRole: role),
                now,
                cancellationToken);
    }

    /// <summary>
    /// A cancel this server sent to the order (a row that came to <c>UNSETTLED</c> from <c>CANCEL_SENT</c>) is settled in its audit
    /// attempt as the supervision's own ending settles it (review L1, incremental review L-1): CONFIRMED when RIoT reads the order
    /// CANCELLED, FAILED when it reads another ending, UNKNOWN when it reads none -- the order is gone or unreadable, and the
    /// supervision's <c>Outcome</c> says UNKNOWN for a missing state too. An attempt already settled is left alone. Staged; the
    /// caller saves. Answers what the attempt was settled as, or null.
    /// </summary>
    private async Task<string?> SettleCancelAsync(ForeignRiotOrderRow row, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (row.CancelCommandAuditId is not { } auditId)
        {
            return null;
        }
        if (row.EndedOrderState is int ended)
        {
            row.CancelResult = ended == RiotOrderState.Cancelled
                ? ForeignRiotOrderCancelResults.Cancelled
                : ForeignRiotOrderCancelResults.EndedOtherwise;
            row.CancelResultAt = now;
        }
        RiotOrderCommandAuditRow? attempt = await dbContext.RiotOrderCommandAudit
            .SingleOrDefaultAsync(item => item.CommandAuditId == auditId, cancellationToken).ConfigureAwait(false);
        if (attempt is null or { Outcome: RiotOrderCommandOutcome.Confirmed or RiotOrderCommandOutcome.Failed })
        {
            return null;
        }
        attempt.Outcome = row.EndedOrderState switch
        {
            RiotOrderState.Cancelled => RiotOrderCommandOutcome.Confirmed,
            null => RiotOrderCommandOutcome.Unknown,
            _ => RiotOrderCommandOutcome.Failed,
        };
        attempt.ReconciledAt = now;
        return attempt.Outcome.ToString();
    }

    private async Task<RiotFacts> ReadRiotAsync(string orderId, CancellationToken cancellationToken)
    {
        RiotReadOutsideWriteLock.Ensure(dbContext);
        RiotUnfinishedOrderListing? listing;
        try
        {
            listing = await orders.ListUnfinishedOrdersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Any failure of the read is "could not read" (review L3): refused and audited, never a 500 without a record.
            listing = null;
        }
        RiotListedOrder? listed = listing?.Orders.FirstOrDefault(
            order => string.Equals(order.OrderId, orderId, StringComparison.Ordinal));

        RiotOrderStateReading? reading;
        try
        {
            reading = await orders.ReadOrderStateAsync(orderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            reading = null;
        }
        return new RiotFacts(
            listing?.IsComplete == true,
            listing?.IncompleteReason,
            listed is not null,
            listed?.OrderState,
            listed?.ExecuteVehicleKey,
            reading?.OrderState,
            timeProvider.GetUtcNow());
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>这一次从 RIoT 读到的，原样进审计。</summary>
    private sealed record RiotFacts(
        bool ListingComplete,
        string? ListingIncompleteReason,
        bool Listed,
        int? ListedState,
        string? ListedExecuteVehicleKey,
        int? ReadState,
        DateTimeOffset ReadAt);
}
