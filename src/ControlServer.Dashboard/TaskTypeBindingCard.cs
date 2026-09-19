using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>任务类型绑定与暂停（control-server#162）。</summary>
public sealed class TaskTypeBindingCard : IDashboardCard
{
    public string CardId => "task-type-bindings";

    public string Title => "任务类型绑定与暂停";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "task-type-bindings";

    public string RenderFact(JsonElement fact) => string.Empty;
}
