using System.Globalization;

namespace ControlServer.Dashboard;

/// <summary>
/// 人工判故障（REQ-0359，control-server#384）：期待动作超时卡片的某一行，管理员到现场确认那个仓的锁、光幕或门机构坏了，
/// 判它故障，让这次装卸停下转人工恢复。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么它能进「只放收紧方向」的名单。</b>判定不开门、不结算、不取消需求、不改 MES：车载端应用判定后，那个仓在
/// <c>OperationResult</c> 里报 <c>UNKNOWN</c>，这次装卸与它的需求进 <c>RecoveryRequired</c>，只能经异常恢复离开。它让一件事停下，
/// 不让任何一件事继续，属 fail-safe 方向（规格 5.7）。
/// </para>
/// <para>
/// <b>「无人员认证」那道口子由什么挡。</b>看板只转交。服务端的判定接口（control-server#383）自己判全部前置条件——这辆车在装卸、
/// 这个仓正是它在等的那个、车报了期待动作超时、操作既没闭环也没判过 <c>UNKNOWN</c>——并且要判故障入口自己的共享凭据：
/// 确认页上那个不回显的凭据框只作为转交请求的 <c>Authorization: Bearer</c> 头，看板不存它、不在链接里带它。谁判的来自管理员
/// 填的工号，原样记录（CP-0005 第 8 节：账户落地之前，入口取共享凭据加个人工号）。入口在现场没打开时服务端没有这条路由，
/// 提交得到 404。
/// </para>
/// <para>
/// <b>每次提交一个新的 <c>requestId</c>。</b>同一次判定重复提交会被服务端以「本次装卸已有待答判定」拒绝（409），看板如实显示；
/// 服务端没在 10 秒内应答时，确认页让人回主页看那一行的状态，不让人盲目重交。
/// </para>
/// </remarks>
public sealed class SlotFaultDeclarationAction : IDashboardAction
{
    public const string Id = "slot-fault-declaration";

    /// <summary>服务端判定接口的路由，与 <c>SlotFaultDeclarationEndpoints.Route</c> 是同一个（测试对着两边比）。</summary>
    public const string Route = "/api/safety/v1/slot-fault-declarations";

    public string ActionId => Id;

    public string Title => "人工判故障";

    public string TargetPath => Route;

    public IReadOnlyList<DashboardActionField> Fields { get; } =
    [
        new("agvId", "车", DashboardActionFieldKind.Hidden, Required: true),
        new("slotNo", "仓位", DashboardActionFieldKind.Hidden, Required: true),
        new("faultCategory", "故障类别（必选）", DashboardActionFieldKind.Choice, Required: true,
        [
            new("LOCK", "锁"),
            new("LIGHT_CURTAIN", "仓内光幕"),
            new("DOOR_MECHANISM", "门机构"),
            new("IO_MODULE", "IO 模块"),
        ]),
        new("note", "现场说明（必填）", DashboardActionFieldKind.TextArea, Required: true),
        new("operatorId", "管理员工号（必填，原样记录）", DashboardActionFieldKind.Text, Required: true),
        new("administratorRole", "管理员角色（必选）", DashboardActionFieldKind.Choice, Required: true,
        [
            new("MAINTENANCE_ADMINISTRATOR", "维护管理员"),
            new("SYSTEM_ADMINISTRATOR", "系统管理员"),
        ]),
        new("credential", "判故障入口凭据（必填，不回显，不记录）", DashboardActionFieldKind.BearerCredential, Required: true),
    ];

    /// <summary>The link a row of the expected-action-overdue card carries to this action's confirmation page.</summary>
    public static string Link(string agvId, string slotNo) =>
        $"/actions/{Id}?agvId={Uri.EscapeDataString(agvId)}&slotNo={Uri.EscapeDataString(slotNo)}";

    /// <summary>
    /// 判定接口拒绝原因码（<c>SlotFaultDeclarationRefusals</c>）的中文说明。看板不引用服务端程序集，所以码在这里照抄一遍；
    /// <c>DashboardActionTests</c> 按反射核对服务端的每一个码这里都有说明。
    /// </summary>
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["SLOT_FAULT_NO_SLOT_OPERATION_IN_PROGRESS"] = "这辆车此刻没有在装货或卸货",
        ["SLOT_FAULT_SLOT_NOT_IN_OPERATION"] = "这个仓不在这次装卸的仓位里",
        ["SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE"] = "车没有报这个仓期待动作超时",
        ["SLOT_FAULT_NOT_THE_CURRENT_SLOT"] = "车正在等的是另一个仓",
        ["SLOT_FAULT_OPERATION_ALREADY_CLOSED"] = "这次装卸已经有结果（完成、失败或已取消）",
        ["SLOT_FAULT_OPERATION_ALREADY_UNKNOWN"] = "这次装卸已经判为结果不明、在等异常处置",
        ["SLOT_FAULT_DECLARATION_PENDING"] = "这次装卸已有一条判定在等车载端答复",
        ["SLOT_FAULT_LOAD_CANCELLATION_IN_PROGRESS"] = "这次装货已授权取消、车正在证明仓是空的，取消就是它的结局",
        ["SLOT_FAULT_NO_SESSION"] = "这辆车从没和服务端建立过会话",
        ["SLOT_FAULT_REQUEST_ID_REUSED"] = "请求号被另一份内容用过",
    };

    public string? DescribeReason(string reasonCode) => Reasons.GetValueOrDefault(reasonCode);

    public object BuildRequest(IReadOnlyDictionary<string, string> form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new
        {
            requestId = Guid.NewGuid().ToString("D"),
            agvId = form.GetValueOrDefault("agvId") ?? string.Empty,
            // Not a number: sent as null, which the server answers with 422 -- a malformed link is not guessed at.
            slotNo = int.TryParse(form.GetValueOrDefault("slotNo"), NumberStyles.None, CultureInfo.InvariantCulture, out int slotNo)
                ? slotNo
                : (int?)null,
            faultCategory = form.GetValueOrDefault("faultCategory") ?? string.Empty,
            note = form.GetValueOrDefault("note") ?? string.Empty,
            operatorId = form.GetValueOrDefault("operatorId") ?? string.Empty,
            administratorRole = form.GetValueOrDefault("administratorRole") ?? string.Empty
        };
    }
}
