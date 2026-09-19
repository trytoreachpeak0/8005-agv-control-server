namespace ControlServer.Dashboard;

/// <summary>Hold one Map + TASK_TYPE (control-server#162).</summary>
public sealed class TaskTypeHoldAction : IDashboardAction
{
    public string ActionId => TaskTypeBindingCard.HoldActionId;

    public string Title => "暂停任务类型";

    public string TargetPath => "/api/task-type-holds";

    public IReadOnlyList<DashboardActionField> Fields => [];

    public object BuildRequest(IReadOnlyDictionary<string, string> form) => new { };
}
