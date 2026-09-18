using System.Text.Json;

namespace ControlServer.Dashboard;

public sealed class ExpectedActionOverdueCard : IDashboardCard
{
    public string CardId => "expected-action-overdue";

    public string Title => "期待动作超时";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "expected-action-overdue";

    public string RenderFact(JsonElement fact) => throw new NotImplementedException();
}
