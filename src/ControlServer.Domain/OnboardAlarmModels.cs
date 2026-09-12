namespace ControlServer.Domain;

/// <summary>
/// 一条车载告警与那台车正在做的事的关系（REQ-0270 的服务端半边）。
/// </summary>
/// <remarks>
/// **本期没有人员认证，所以「按访问模型收敛」不是按人分权，是按「哪个界面看得到什么」分。**与当前
/// AGV／当前停靠／当前操作直接相关的显示在对应车载端界面（那一半在 onboard-hmi）；看板集中显示全部，
/// 不按这个枚举过滤（见 <see cref="OnboardAlarmDashboardVisibility"/>）。所以这个枚举描述的是关系，
/// 不是权限：求值时不需要也不接受任何身份输入。把它做成需要身份才能求值的权限判断，会让它落在一个
/// 全场共用环境变量密钥的地基上。
/// </remarks>
public enum OnboardAlarmScope
{
    /// <summary>与那台车本身直接相关。车载端界面显示，看板也显示。</summary>
    CurrentVehicle,

    /// <summary>与车当前停靠的站点直接相关。车载端界面显示，看板也显示。</summary>
    CurrentStop,

    /// <summary>与车当前正在执行的那次操作直接相关。车载端界面显示，看板也显示。</summary>
    CurrentOperation,

    /// <summary>与以上三者都不直接相关。只在看板显示。</summary>
    Fleet
}

/// <summary>
/// 一条车载告警，服务端侧。
/// </summary>
/// <remarks>
/// <see cref="AlarmCode"/> **是字符串，不是 <c>ErrorCode</c>**。告警是开放集合，<c>ErrorCode</c> 是
/// 封闭 enum，把开放集合塞进封闭 enum 等于每加一个故障码就发一次 breaking change。有架构测试断言这个
/// 属性是 <c>string</c>，并且服务端收到的告警码集合与协议错误码注册表无交集。
///
/// <see cref="AlarmId"/> 是线上那条告警的身份，原样存下不做解释。服务端不靠它跨快照做关联——快照
/// 整份取代，本来就不需要——但一份线上事实在落库的路上被悄悄丢掉是另一回事。它是可空的尾参，
/// #16 已有的构造点一个都不用改。
/// </remarks>
public sealed record OnboardAlarmEntry(
    string AlarmCode,
    string Severity,
    DateTimeOffset RaisedAt,
    OnboardAlarmScope Scope,
    string Message,
    string? DemandId = null,
    string? StationId = null,
    string? SlotOperationAttemptId = null,
    int? PhysicalSlotNumber = null,
    string? AlarmId = null);

/// <summary>
/// 一台车当前全量告警的一份快照。
/// </summary>
/// <remarks>
/// **快照而不是事件流。**后一份整体取代前一份，不做增量合并——事件流断线重连那段正是 REQ-0269 禁止
/// 的东西：重连后不知道漏了什么，只能显示一个不确定新旧的旧值。快照没有这个问题，每一份都是当下的
/// 全部事实。所以这个类型上没有任何一处能承载「上次是什么」。
/// </remarks>
public sealed record OnboardAlarmSnapshotView(
    string AgvId,
    long SnapshotSequence,
    DateTimeOffset CapturedAt,
    IReadOnlyList<OnboardAlarmEntry> Alarms)
{
    /// <summary>交付类别。它是 SNAPSHOT，不是 RELIABLE，也不是事件。</summary>
    public const string DeliveryClass = "SNAPSHOT";
}

/// <summary>
/// 哪些告警进看板：**全部**。
/// </summary>
/// <remarks>
/// <para>
/// REQ-0270 原文：「与当前 AGV、当前停靠或当前操作直接相关的告警显示在对应 OnboardHmi；**全部 8005 告警集中显示在
/// ControlServer**」。收敛只作用在车载端那一侧——车上的人只看与这台车当下有关的；看板是集中显示，一条都不少。
/// </para>
/// <para>
/// 2026-09-12 之前这里只放行 <see cref="OnboardAlarmScope.Fleet"/>，把收敛规则同时套在了两侧，与原文不符；产品负责人
/// 当日确认按原文改为全部。<see cref="OnboardAlarmEntry.Scope"/> 仍然照存，它回答的是「这条告警与车的关系」，车载端
/// 本机界面据此收敛，看板不据此过滤。
/// </para>
/// <para>求值入参仍然只有一份快照：没有身份，没有密钥，没有角色。</para>
/// </remarks>
public static class OnboardAlarmDashboardVisibility
{
    public static IReadOnlyList<OnboardAlarmEntry> ForDashboard(OnboardAlarmSnapshotView snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return [.. snapshot.Alarms];
    }
}

/// <summary>
/// 一台车在看板上的告警投影：要么是当下的告警，要么是拿不到它的原因。
/// </summary>
/// <remarks>
/// 没有第三种。车辆失联时 <see cref="UnavailableReason"/> 有值而 <see cref="Alarms"/> 是空的——看板
/// 显示的是「失联」这个原因，不是那台车失联前的最后一批告警（REQ-0269）。
/// </remarks>
public sealed record VehicleAlarmProjection(
    string AgvId,
    long SnapshotSequence,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<OnboardAlarmEntry> Alarms,
    string? UnavailableReason)
{
    public const string LinkDownReason = "车辆失联";

    public const string NeverReportedReason = "尚未收到该车快照";

    public bool IsAvailable => UnavailableReason is null;
}
