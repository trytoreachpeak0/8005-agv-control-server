using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>现场人工释放一个站点独占的请求（control-server#419）。文本字段可空：缺了是一条拒绝理由，不是格式错误。</summary>
/// <param name="VehicleKey">操作员认定的持有车。必须就是这一行的持有车——点名，不推断。</param>
/// <param name="SiteVerification">现场核实「车不在站上」的记录引用（照片、巡检单号等）。</param>
/// <param name="ClaimedRole">操作员自述的角色，照录，不认证任何东西。</param>
public sealed record StationExclusivityManualReleaseRequest(
    int MapId,
    int StationId,
    string? VehicleKey,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole = null);

/// <summary>人工释放的结果。<see cref="Released"/> 为真时 <see cref="Holder"/> 是被释放的那一次独占（释放前读到的样子）。</summary>
public sealed record StationExclusivityManualReleaseResult(
    bool Released,
    IReadOnlyList<string> Codes,
    StationExclusivity? Holder,
    string RiotCrossCheck,
    string AuditRecordId);

/// <summary>
/// 站点独占的人工释放（批次8 跟进，control-server#419）：持有车离线、被拖走或退役时，公共站点（<c>FIXED_TASK_STATION</c>）与等待点
/// （<c>WAITING_POINT</c>）的独占行没有自动出口——离点清扫要求车在线且 RIoT 报出别的站（fail-closed，这个方向不变）。这里是受治理的人工出口。
/// </summary>
/// <remarks>
/// <para>
/// <b>治理与等待点导入同一套</b>（control-server#388）：具名操作员、理由、现场核实记录，每一次请求——拒绝也算——写一条管理员审计。
/// 充电桩（<c>CHARGER</c>）不在这里：它走批次 9 的人工清桩（control-server#406），那条路还要处理旧充电单与桩的暂停。
/// </para>
/// <para>
/// <b>与离点清扫同一个删除条件</b>（<see cref="FixedStationExclusivity.ReleaseAsReadAsync"/>，带读到的 <c>JourneyId</c> 与
/// <c>RecordId</c>），所以两者同刻只有一方生效，由数据库裁决：后到的一方删到 0 行，拒绝码 <see cref="HolderChanged"/>，不写任何释放。
/// 读到之后被交给了同一辆车的新旅程，同样拒绝——那一次已经不是操作员核实的那一次。
/// </para>
/// <para>
/// <b>持有者仍在途、仍以这个站为未完成停靠时拒绝</b>（<see cref="HolderJourneyStillBound"/>）：放了之后下一轮补预占会把站原样还给它，
/// 或者旅程以为自己还站在点上。没有旅程行的持有者（cs#389 的空闲返回只写用途占有与等待点预占）以用途占有为准：持有车在同一个
/// <c>JourneyId</c> 上还占着，就是仍在途（#422 审查必修 1）。先把那趟旅程收尾（故障人工恢复入口），再来释放。
/// </para>
/// <para>
/// <b>阻断的旅程只在车已占用这个站（<c>OCCUPIED</c>）时可以放</b>：清扫对阻断一律不放，那正是要人来判的情况。阻断在路上
/// （<c>RESERVED</c>、这个站仍是未完成停靠）拒绝（<see cref="HolderJourneyBlockedOnApproach"/>）——修好后恢复，车会照原单开往
/// 已经放给别的车的站；先用故障恢复入口的放弃出口把那趟旅程收尾。已完成的旅程可以放。
/// </para>
/// <para>
/// <b>服务端在线时</b>（Host 接口）另读一次 RIoT：车在线且报在这个站上，与现场核实冲突，拒绝（<see cref="VehicleReportedAtStation"/>）。
/// 读不到、离线、报在别处都不挡——离线正是这条出口存在的理由。服务端停着时（FieldOps 直接写库）没有这项交叉核对，结果里如实写
/// <see cref="CrossCheckNotAvailable"/>。
/// </para>
/// <para>
/// 只动这一行与它的经过（释放原因 <see cref="ReleasedByOperator"/>）。持有车的用途占有不动：那是车自己的事，由它的旅程收尾或故障恢复处理。
/// </para>
/// </remarks>
public static class StationExclusivityManualRelease
{
    /// <summary>经过里的释放原因：现场人工释放。</summary>
    public const string ReleasedByOperator = "RELEASED_BY_OPERATOR";

    /// <summary>每一次请求写的管理员审计动作。</summary>
    public const string AuditAction = "STATION_EXCLUSIVITY_MANUAL_RELEASE";

    public const string OperatorRequired = "OPERATOR_REQUIRED";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string SiteVerificationRequired = "SITE_VERIFICATION_REQUIRED";
    public const string VehicleRequired = "VEHICLE_REQUIRED";
    public const string FieldTooLong = "FIELD_TOO_LONG";
    public const string StationNotHeld = "STATION_NOT_HELD";
    public const string KindNotReleasable = "STATION_KIND_NOT_RELEASABLE_HERE";
    public const string HolderMismatch = "HOLDER_VEHICLE_MISMATCH";
    public const string HolderJourneyStillBound = "HOLDER_JOURNEY_STILL_BOUND";
    public const string HolderJourneyBlockedOnApproach = "HOLDER_JOURNEY_BLOCKED_ON_APPROACH";
    public const string VehicleReportedAtStation = "VEHICLE_REPORTED_AT_STATION";
    public const string HolderChanged = "HOLDER_CHANGED_SINCE_READ";

    public const string CrossCheckNotAvailable = "NOT_AVAILABLE";
    public const string CrossCheckUnreadable = "UNREADABLE";
    public const string CrossCheckOffline = "OFFLINE";
    public const string CrossCheckNoStation = "NO_CURRENT_STATION";
    public const string CrossCheckAtOtherStation = "AT_OTHER_STATION";
    public const string CrossCheckAtThisStation = "AT_THIS_STATION";

    /// <summary>理由与现场核实记录的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>车号、操作员与自述角色的最长长度。</summary>
    public const int MaxIdentifierLength = 64;

    /// <summary>这里能释放的两类。</summary>
    public static IReadOnlyList<string> ReleasableKinds { get; } =
        [StationExclusivityKinds.FixedTaskStation, StationExclusivityKinds.WaitingPoint];

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>
    /// 核对并释放。<paramref name="vehicleFacts"/> 为空表示读不了 RIoT（服务端停着、FieldOps 直接写库）。
    /// </summary>
    public static async Task<StationExclusivityManualReleaseResult> ReleaseAsync(
        ControlServerDbContext dbContext,
        IGovernanceAuditWriter audit,
        IRiotVehicleFacts? vehicleFacts,
        StationExclusivityManualReleaseRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(request);

        string? vehicleKey = Trimmed(request.VehicleKey);
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? siteVerification = Trimmed(request.SiteVerification);
        string? claimedRole = Trimmed(request.ClaimedRole);

        List<string> codes = [];
        if (operatorId is null) codes.Add(OperatorRequired);
        if (reason is null) codes.Add(ReasonRequired);
        if (siteVerification is null) codes.Add(SiteVerificationRequired);
        if (vehicleKey is null) codes.Add(VehicleRequired);
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (siteVerification?.Length ?? 0) <= MaxTextLength &&
                    (vehicleKey?.Length ?? 0) <= MaxIdentifierLength && (operatorId?.Length ?? 0) <= MaxIdentifierLength &&
                    (claimedRole?.Length ?? 0) <= MaxIdentifierLength;
        if (!fits) codes.Add(FieldTooLong);

        // Only what fits the limits is kept in the audit; anything longer is recorded by its length.
        object Said() => fits
            ? new { vehicleKey, operatorId, reason, siteVerification, claimedRole }
            : new
            {
                vehicleKeyLength = vehicleKey?.Length,
                operatorIdLength = operatorId?.Length,
                reasonLength = reason?.Length,
                siteVerificationLength = siteVerification?.Length,
                claimedRoleLength = claimedRole?.Length
            };
        string? auditRole = fits ? claimedRole : null;

        StationExclusivityRow? held = await dbContext.Set<StationExclusivityRow>().AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MapId == request.MapId && row.StationId == request.StationId, cancellationToken)
            .ConfigureAwait(false);
        if (held is null)
        {
            codes.Add(StationNotHeld);
        }
        else
        {
            if (!ReleasableKinds.Contains(held.StationKind, StringComparer.Ordinal)) codes.Add(KindNotReleasable);
            if (vehicleKey is not null && !string.Equals(held.VehicleKey, vehicleKey, StringComparison.Ordinal))
            {
                codes.Add(HolderMismatch);
            }
        }
        if (codes.Count > 0)
        {
            return await RefuseAsync(CrossCheckNotAvailable).ConfigureAwait(false);
        }

        string crossCheck = await CrossCheckAsync(dbContext, vehicleFacts, held!, cancellationToken).ConfigureAwait(false);
        if (crossCheck == CrossCheckAtThisStation) codes.Add(VehicleReportedAtStation);
        if (await HolderBindingAsync(dbContext, held!, cancellationToken).ConfigureAwait(false) is { } binding)
        {
            codes.Add(binding);
        }
        if (codes.Count > 0)
        {
            return await RefuseAsync(crossCheck).ConfigureAwait(false);
        }

        IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        string auditRecordId;
        await using (transaction)
        {
            if (!await FixedStationExclusivity.ReleaseAsReadAsync(dbContext, held!, now, ReleasedByOperator, cancellationToken)
                    .ConfigureAwait(false))
            {
                codes.Add(HolderChanged);
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                auditRecordId = await WriteAuditAsync(GovernanceActionOutcome.Succeeded, "RELEASED", crossCheck)
                    .ConfigureAwait(false);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                return new StationExclusivityManualReleaseResult(true, [], ToModel(held!), crossCheck, auditRecordId);
            }
        }
        return await RefuseAsync(crossCheck).ConfigureAwait(false);

        async Task<StationExclusivityManualReleaseResult> RefuseAsync(string checkedAgainstRiot)
        {
            string id = await WriteAuditAsync(GovernanceActionOutcome.Failed, "REJECTED", checkedAgainstRiot)
                .ConfigureAwait(false);
            return new StationExclusivityManualReleaseResult(
                false, [.. codes], held is null ? null : ToModel(held), checkedAgainstRiot, id);
        }

        Task<string> WriteAuditAsync(GovernanceActionOutcome outcome, string result, string checkedAgainstRiot) =>
            audit.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    AuditAction,
                    GovernedObjectKind.StationExclusivity,
                    ObjectId(request.MapId, request.StationId),
                    null,
                    outcome,
                    JsonSerializer.Serialize(
                        new
                        {
                            mapId = request.MapId,
                            stationId = request.StationId,
                            said = Said(),
                            holder = held is null
                                ? null
                                : new
                                {
                                    stationKind = held.StationKind,
                                    state = held.State,
                                    vehicleKey = held.VehicleKey,
                                    journeyId = held.JourneyId,
                                    recordId = held.RecordId,
                                    stateSince = held.StateSince,
                                    waitingPointVersion = held.WaitingPointVersion
                                },
                            riotCrossCheck = checkedAgainstRiot,
                            codes,
                            result
                        },
                        DetailOptions),
                    ClaimedAdministratorRole: auditRole),
                now,
                cancellationToken);
    }

    /// <summary>审计对象号：<c>map/station</c>。</summary>
    public static string ObjectId(int mapId, int stationId) =>
        FormattableString.Invariant($"{mapId}/{stationId}");

    private static async Task<string> CrossCheckAsync(
        DbContext dbContext, IRiotVehicleFacts? vehicleFacts, StationExclusivityRow held, CancellationToken cancellationToken)
    {
        // control-server#452: the only caller today, the Host endpoint, holds no transaction here; the guard keeps it that way.
        RiotReadOutsideWriteLock.Ensure(dbContext);
        if (vehicleFacts is null)
        {
            return CrossCheckNotAvailable;
        }
        RiotVehicleObservation vehicle;
        try
        {
            vehicle = await vehicleFacts.ReadVehicleAsync(held.VehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An unreachable vehicle is the very case this exit exists for; the site verification carries it.
            return CrossCheckUnreadable;
        }
        return vehicle switch
        {
            { Connected: false } => CrossCheckOffline,
            { CurrentStationId: null } => CrossCheckNoStation,
            { CurrentStationId: int current } when current == held.StationId => CrossCheckAtThisStation,
            _ => CrossCheckAtOtherStation
        };
    }

    // The first half of the sweep's departure evidence, read the same way, with two differences (#422 review).
    //
    // No journey row is not "nothing holds it": an idle return (control-server#389) commits with a purpose claim and a
    // WAITING_POINT reservation under one JourneyId and no JourneyRuntimes row. While the holder's claim on that JourneyId
    // stands the vehicle is still on its way, so releasing would let a second vehicle into the point it is driving to.
    //
    // A blocked journey may be released only where it stands (OCCUPIED). Blocked on its way (RESERVED) with the station
    // still an unfinished stop, it would drive on to it after a repair resumes it; that journey is taken out through the
    // fault recovery entry's give-up action first.
    private static async Task<string?> HolderBindingAsync(
        ControlServerDbContext dbContext, StationExclusivityRow held, CancellationToken cancellationToken)
    {
        JourneyRuntimeRow? journey = await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleOrDefaultAsync(item => item.JourneyId == held.JourneyId, cancellationToken).ConfigureAwait(false);
        if (journey is null)
        {
            bool claimed = await dbContext.Set<VehiclePurposeClaimRow>().AsNoTracking()
                .AnyAsync(claim => claim.VehicleKey == held.VehicleKey && claim.JourneyId == held.JourneyId, cancellationToken)
                .ConfigureAwait(false);
            return claimed ? HolderJourneyStillBound : null;
        }
        if (journey.Stage == JourneyRuntimeStage.Completed)
        {
            return null;
        }
        bool stillAhead = await dbContext.Set<JourneyStopRow>().AsNoTracking()
            .AnyAsync(stop => stop.JourneyId == held.JourneyId &&
                              stop.StationRiotId == held.StationId &&
                              stop.Status != JourneyStopStatuses.Completed &&
                              stop.Status != JourneyStopStatuses.Removed,
                cancellationToken)
            .ConfigureAwait(false);
        if (!stillAhead)
        {
            return null;
        }
        if (journey.Stage != JourneyRuntimeStage.Blocked)
        {
            return HolderJourneyStillBound;
        }
        return held.State == StationExclusivityStates.Occupied ? null : HolderJourneyBlockedOnApproach;
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static StationExclusivity ToModel(StationExclusivityRow row) =>
        new(row.MapId, row.StationId, row.StationKind, row.State, row.VehicleKey, row.JourneyId, row.StateSince,
            row.WaitingPointVersion, row.ChargerRosterVersion);
}
