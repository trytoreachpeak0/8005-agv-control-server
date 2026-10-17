using System.Text.Json;

namespace ControlServer.Dashboard;

public sealed class StationTimeoutSuppressionCard : IDashboardCard
{
    public string CardId => "station-timeout-suppressions";

    public string Title => "站点超时抑制清单";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "station-timeout-suppressions";

    public string RenderFact(JsonElement fact) => "";
}
