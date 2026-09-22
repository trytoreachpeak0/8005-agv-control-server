using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>车队视图的「等人中的旅程」卡片（control-server#273）。</summary>
public sealed class WaitingJourneyCard : IDashboardCard
{
    public string CardId => "waiting-journeys";

    public string Title => "等人中的旅程";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "waiting-journeys";

    public string RenderFact(JsonElement fact)
    {
        _ = fact;
        return string.Empty;
    }
}
