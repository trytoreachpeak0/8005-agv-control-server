using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 解除路网黏住陈旧原因的请求（control-server#659）。文本字段可空：缺了是一条拒绝理由，不是格式错误。
/// </summary>
/// <remarks>
/// 后四项是操作员看过、确认过的那份快照头（先用 GET 读出来）：陈旧原因、陈旧起始时刻、当前边组指纹、当前动态代价有无。四项必须与库里逐项一致，
/// 解除的才是操作员核实的那一份——不是读完之后又变了的。
/// </remarks>
/// <param name="SiteVerification">核实记录的引用：在 RIoT 地图侧核对边组改动或动态代价条目的记录号、截图等。</param>
/// <param name="ClaimedRole">操作员自述的角色，照录，不认证任何东西。</param>
public sealed record RouteGraphStickyStaleReleaseRequest(
    int MapId,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole,
    string? ExpectedStaleReason,
    DateTimeOffset? ExpectedStaleSince,
    string? AcknowledgedEdgeGroupFingerprint,
    bool? AcknowledgedDynamicRouteCostPresent);

/// <summary>解除的结果。<see cref="Snapshot"/> 是判定时读到的快照头（解除前的样子），没有快照时为空。</summary>
public sealed record RouteGraphStickyStaleReleaseResult(
    bool Released,
    IReadOnlyList<string> Codes,
    RouteGraphSnapshotHeader? Snapshot,
    string AuditRecordId);

/// <summary>解除入口的拒绝码与它们给现场人员看的中文说明。</summary>
public static class RouteGraphStickyStaleReleaseCodes
{
    public const string OperatorRequired = "ROUTE_GRAPH_RELEASE_OPERATOR_REQUIRED";
    public const string ReasonRequired = "ROUTE_GRAPH_RELEASE_REASON_REQUIRED";
    public const string SiteVerificationRequired = "ROUTE_GRAPH_RELEASE_SITE_VERIFICATION_REQUIRED";
    public const string AcknowledgementRequired = "ROUTE_GRAPH_RELEASE_ACKNOWLEDGEMENT_REQUIRED";
    public const string FieldTooLong = "ROUTE_GRAPH_RELEASE_FIELD_TOO_LONG";
    public const string SnapshotNotFound = "ROUTE_GRAPH_RELEASE_SNAPSHOT_NOT_FOUND";
    public const string NotStickyStale = "ROUTE_GRAPH_RELEASE_NOT_STICKY_STALE";
    public const string StateChangedSinceRead = "ROUTE_GRAPH_RELEASE_STATE_CHANGED_SINCE_READ";
    public const string DesignNotRefreshedSinceStale = "ROUTE_GRAPH_RELEASE_DESIGN_NOT_REFRESHED_SINCE_STALE";

    /// <summary>每一个拒绝码给现场人员看的中文说明：Host 入口的拒绝响应里逐条带上（<c>descriptions</c>）。</summary>
    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [OperatorRequired] = "没有写办理人（operatorId）",
        [ReasonRequired] = "没有写理由（reason）",
        [SiteVerificationRequired] =
            "没有写核实记录（siteVerification）：例如在 RIoT 地图侧核对这次边组改动或动态代价条目的记录号、截图",
        [AcknowledgementRequired] =
            "没有填确认的快照状态：expectedStaleReason、expectedStaleSince、acknowledgedEdgeGroupFingerprint、"
            + "acknowledgedDynamicRouteCostPresent 四项都要照 GET 读到的值填",
        [FieldTooLong] = "有一项写得太长：理由与核实记录各不超过 500 字，办理人、角色各不超过 64 字，指纹不超过 128 字",
        [SnapshotNotFound] = "这张图还没有路网快照：服务端没有为这个 mapId 刷新过路网，请核对 mapId",
        [NotStickyStale] =
            "路网眼下不是因为边组指纹变化或动态代价出现而停用：其它陈旧原因（过期、刷新失败）会随下一次刷新自己恢复，不需要人来解除",
        [StateChangedSinceRead] =
            "路网快照在你读出之后变了（陈旧原因、陈旧起始时刻、边组指纹或动态代价有无与你确认的不一致）：请重新 GET，核实新的状态后再办",
        [DesignNotRefreshedSinceStale] =
            "路网图还没有在这次变化之后重读过：现在放行，用的还是变化之前那张图。设计态最多 10 分钟重读一次，请稍后再 GET、再办",
    };
}

/// <summary>
/// 路网黏住陈旧原因的人工解除（control-server#659）：边组指纹变化（<c>ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED</c>）与动态代价由空变非空
/// （<c>ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED</c>）按设计不会自己消失，等多久、刷新多少次都不清——变的是世界，不是图的新旧。这里是受治理的人工出口。
/// </summary>
/// <remarks>
/// <para>
/// <b>治理与站点独占人工释放同一套</b>（control-server#419、#483）：具名操作员、理由、核实记录，每一次请求——拒绝也算——写一条管理员审计。
/// </para>
/// <para>
/// <b>确认的是世界的状态，不只是原因串。</b>陈旧原因只有一列，两条黏住原因先后出现时后一条覆盖前一条（<c>MarkStaleAsync</c>）。所以操作员要连同当前的
/// 边组指纹与动态代价有无一起确认：不论库里留的是哪一条原因，两件事都被人看过。
/// </para>
/// <para>
/// <b>图要在变化之后重读过</b>（<see cref="RouteGraphStickyStaleReleaseCodes.DesignNotRefreshedSinceStale"/>）：设计态的刷新时刻严格晚于陈旧起始时刻，
/// 否则放行的仍是变化之前的那张图。相等也不算：检测到变化的那一轮里，设计态是在读边组之前读的。
/// </para>
/// <para>
/// <b>清除是一次带条件的更新</b>：条件是原因、边组指纹与动态代价有无仍是读到的样子。刷新器同刻改了其中任何一项，更新到 0 行，拒绝码
/// <see cref="RouteGraphStickyStaleReleaseCodes.StateChangedSinceRead"/>，什么也不清。陈旧起始时刻在黏住期间只有解除能改，所以按读到的值核对就够了。
/// </para>
/// <para>
/// 解除只把陈旧原因与起始时刻置空。之后的判定照旧：<c>RouteGraphAccess</c> 按时钟重判过期，下一次刷新失败照常标陈旧。动态代价仍在时放行等于接受它：
/// 检测只看由空变非空，它不会因为仍然存在而再次触发。
/// </para>
/// </remarks>
public static class RouteGraphStickyStaleRelease
{
    /// <summary>每一次请求写的管理员审计动作。</summary>
    public const string AuditAction = "ROUTE_GRAPH_STICKY_STALE_RELEASE";

    /// <summary>理由与核实记录的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>操作员与自述角色的最长长度。</summary>
    public const int MaxIdentifierLength = 64;

    /// <summary>确认的边组指纹的最长长度。指纹是 SHA-256 的十六进制串（64 位），留出余量。</summary>
    public const int MaxFingerprintLength = 128;

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>读一张图的快照头，给操作员核对用。没有快照时为空。</summary>
    public static async Task<RouteGraphSnapshotHeader?> ReadAsync(
        ControlServerDbContext dbContext,
        int mapId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        RouteGraphSnapshotRow? row = await dbContext.RouteGraphSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(snapshot => snapshot.MapId == mapId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToHeader(row);
    }

    /// <summary>核对并解除。</summary>
    public static async Task<RouteGraphStickyStaleReleaseResult> ReleaseAsync(
        ControlServerDbContext dbContext,
        IGovernanceAuditWriter audit,
        RouteGraphStickyStaleReleaseRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(request);

        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? siteVerification = Trimmed(request.SiteVerification);
        string? claimedRole = Trimmed(request.ClaimedRole);
        string? expectedReason = Trimmed(request.ExpectedStaleReason);
        // The empty fingerprint is a real one (a Map with no edge groups), so only null means "not given".
        string? fingerprint = request.AcknowledgedEdgeGroupFingerprint?.Trim();

        List<string> codes = [];
        if (operatorId is null) codes.Add(RouteGraphStickyStaleReleaseCodes.OperatorRequired);
        if (reason is null) codes.Add(RouteGraphStickyStaleReleaseCodes.ReasonRequired);
        if (siteVerification is null) codes.Add(RouteGraphStickyStaleReleaseCodes.SiteVerificationRequired);
        if (expectedReason is null || request.ExpectedStaleSince is null || fingerprint is null ||
            request.AcknowledgedDynamicRouteCostPresent is null)
        {
            codes.Add(RouteGraphStickyStaleReleaseCodes.AcknowledgementRequired);
        }
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (siteVerification?.Length ?? 0) <= MaxTextLength &&
                    (operatorId?.Length ?? 0) <= MaxIdentifierLength && (claimedRole?.Length ?? 0) <= MaxIdentifierLength &&
                    (expectedReason?.Length ?? 0) <= MaxIdentifierLength && (fingerprint?.Length ?? 0) <= MaxFingerprintLength;
        if (!fits) codes.Add(RouteGraphStickyStaleReleaseCodes.FieldTooLong);

        // Only what fits the limits is kept in the audit; anything longer is recorded by its length.
        object Said() => fits
            ? new
            {
                operatorId,
                reason,
                siteVerification,
                claimedRole,
                expectedStaleReason = expectedReason,
                expectedStaleSince = request.ExpectedStaleSince,
                acknowledgedEdgeGroupFingerprint = fingerprint,
                acknowledgedDynamicRouteCostPresent = request.AcknowledgedDynamicRouteCostPresent
            }
            : new
            {
                operatorIdLength = operatorId?.Length,
                reasonLength = reason?.Length,
                siteVerificationLength = siteVerification?.Length,
                claimedRoleLength = claimedRole?.Length,
                expectedStaleReasonLength = expectedReason?.Length,
                acknowledgedEdgeGroupFingerprintLength = fingerprint?.Length
            };
        string? auditRole = fits ? claimedRole : null;

        RouteGraphSnapshotHeader? snapshot = await ReadAsync(dbContext, request.MapId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            codes.Add(RouteGraphStickyStaleReleaseCodes.SnapshotNotFound);
        }
        else if (!RouteGraphStickyStaleReasons.IsSticky(snapshot.StaleReason))
        {
            codes.Add(RouteGraphStickyStaleReleaseCodes.NotStickyStale);
        }
        else
        {
            if (!codes.Contains(RouteGraphStickyStaleReleaseCodes.AcknowledgementRequired, StringComparer.Ordinal) && fits &&
                !Matches(snapshot, expectedReason!, request.ExpectedStaleSince!.Value, fingerprint!,
                    request.AcknowledgedDynamicRouteCostPresent!.Value))
            {
                codes.Add(RouteGraphStickyStaleReleaseCodes.StateChangedSinceRead);
            }
            if (snapshot.DesignRefreshedAt is null || snapshot.StaleSince is null ||
                snapshot.DesignRefreshedAt.Value <= snapshot.StaleSince.Value)
            {
                codes.Add(RouteGraphStickyStaleReleaseCodes.DesignNotRefreshedSinceStale);
            }
        }
        if (codes.Count > 0)
        {
            return await RefuseAsync().ConfigureAwait(false);
        }

        IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        await using (transaction)
        {
            string staleReason = snapshot!.StaleReason!;
            string edgeGroupFingerprint = snapshot.EdgeGroupFingerprint;
            bool dynamicRouteCostPresent = snapshot.DynamicRouteCostPresent;
            int cleared = await dbContext.RouteGraphSnapshots
                .Where(row => row.MapId == request.MapId &&
                              row.StaleReason == staleReason &&
                              row.EdgeGroupFingerprint == edgeGroupFingerprint &&
                              row.DynamicRouteCostPresent == dynamicRouteCostPresent)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.StaleReason, (string?)null)
                        .SetProperty(row => row.StaleSince, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (cleared == 0)
            {
                codes.Add(RouteGraphStickyStaleReleaseCodes.StateChangedSinceRead);
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // A tracked copy would still read stale; the next read goes to the database.
                foreach (var tracked in dbContext.ChangeTracker.Entries<RouteGraphSnapshotRow>()
                             .Where(entry => entry.Entity.MapId == request.MapId).ToArray())
                {
                    tracked.State = EntityState.Detached;
                }
                string auditRecordId = await WriteAuditAsync(GovernanceActionOutcome.Succeeded, "RELEASED").ConfigureAwait(false);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                return new RouteGraphStickyStaleReleaseResult(true, [], snapshot, auditRecordId);
            }
        }
        return await RefuseAsync().ConfigureAwait(false);

        async Task<RouteGraphStickyStaleReleaseResult> RefuseAsync()
        {
            string id = await WriteAuditAsync(GovernanceActionOutcome.Failed, "REJECTED").ConfigureAwait(false);
            return new RouteGraphStickyStaleReleaseResult(false, [.. codes], snapshot, id);
        }

        Task<string> WriteAuditAsync(GovernanceActionOutcome outcome, string result) =>
            audit.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    AuditAction,
                    GovernedObjectKind.RouteGraphSnapshot,
                    ObjectId(request.MapId),
                    null,
                    outcome,
                    JsonSerializer.Serialize(
                        new
                        {
                            mapId = request.MapId,
                            said = Said(),
                            snapshot = snapshot is null
                                ? null
                                : new
                                {
                                    staleReason = snapshot.StaleReason,
                                    staleSince = snapshot.StaleSince,
                                    edgeGroupFingerprint = snapshot.EdgeGroupFingerprint,
                                    edgeGroupRefreshedAt = snapshot.EdgeGroupRefreshedAt,
                                    dynamicRouteCostPresent = snapshot.DynamicRouteCostPresent,
                                    dynamicRouteCostObservedAt = snapshot.DynamicRouteCostObservedAt,
                                    designRevision = snapshot.DesignRevision,
                                    designRefreshedAt = snapshot.DesignRefreshedAt
                                },
                            // Stated on its own so that "was dynamic cost still in play when this was released" is one lookup:
                            // releasing over it accepts it, and it will not trigger again while it stays (control-server#659).
                            dynamicRouteCostPresentAtRelease = snapshot?.DynamicRouteCostPresent,
                            codes,
                            result
                        },
                        DetailOptions),
                    ClaimedAdministratorRole: auditRole),
                now,
                cancellationToken);
    }

    /// <summary>审计对象号：<c>mapId</c>。</summary>
    public static string ObjectId(int mapId) => FormattableString.Invariant($"{mapId}");

    private static bool Matches(
        RouteGraphSnapshotHeader snapshot,
        string expectedReason,
        DateTimeOffset expectedSince,
        string fingerprint,
        bool dynamicRouteCostPresent) =>
        string.Equals(snapshot.StaleReason, expectedReason, StringComparison.Ordinal) &&
        snapshot.StaleSince == expectedSince &&
        string.Equals(snapshot.EdgeGroupFingerprint, fingerprint, StringComparison.Ordinal) &&
        snapshot.DynamicRouteCostPresent == dynamicRouteCostPresent;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RouteGraphSnapshotHeader ToHeader(RouteGraphSnapshotRow row) => new(
        row.MapId,
        row.DesignRevision,
        row.DesignRefreshedAt,
        row.DesignSourceGmtUpdate,
        row.DesignEdgeCount,
        row.DesignStationCount,
        row.RuntimeRefreshedAt,
        row.RuntimeRemovedEdgeCount,
        row.RuntimeRemovedStationCount,
        row.EdgeGroupFingerprint,
        row.EdgeGroupRefreshedAt,
        row.DynamicRouteCostPresent,
        row.DynamicRouteCostObservedAt,
        row.StaleReason,
        row.StaleSince);
}
