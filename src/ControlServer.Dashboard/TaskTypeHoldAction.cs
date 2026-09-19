using System.Globalization;

namespace ControlServer.Dashboard;

/// <summary>
/// 暂停一个 <c>Map + TASK_TYPE</c>（REQ-0340 收紧半边，control-server#162）：现场发现某个固定站被占作他用或位置不对，立即让依赖它的新任务停下。
/// </summary>
/// <remarks>
/// 服务端的 <c>/api/task-type-holds</c> 只收回环来源，看板与服务端同在控制端，所以转交过去就是回环。理由必填，自报角色原样记下、
/// 不作认证。已建单的 RIoT 订单不受影响。
/// </remarks>
public sealed class TaskTypeHoldAction : IDashboardAction
{
    public string ActionId => TaskTypeBindingCard.HoldActionId;

    public string Title => "暂停任务类型";

    public string TargetPath => "/api/task-type-holds";

    public IReadOnlyList<DashboardActionField> Fields { get; } =
    [
        new("mapId", "Map", DashboardActionFieldKind.Hidden, Required: true),
        new("taskType", "任务类型", DashboardActionFieldKind.Hidden, Required: true),
        new("reason", "暂停理由（必填）", DashboardActionFieldKind.TextArea, Required: true),
        new("claimedRole", "自报角色（可不填，只记录，不作认证）", DashboardActionFieldKind.Text, Required: false),
    ];

    public object BuildRequest(IReadOnlyDictionary<string, string> form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new
        {
            mapId = int.TryParse(form.GetValueOrDefault("mapId"), NumberStyles.None, CultureInfo.InvariantCulture, out int mapId)
                ? mapId
                : 0,
            taskType = form.GetValueOrDefault("taskType") ?? string.Empty,
            reason = form.GetValueOrDefault("reason") ?? string.Empty,
            claimedRole = string.IsNullOrWhiteSpace(form.GetValueOrDefault("claimedRole")) ? null : form["claimedRole"]
        };
    }
}
