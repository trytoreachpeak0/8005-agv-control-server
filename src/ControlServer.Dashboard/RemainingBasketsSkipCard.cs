using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>Stub for the test commit of control-server#830; the implementation commit replaces it.</summary>
public sealed class RemainingBasketsSkipCard : IDashboardCard
{
    public string CardId => "remaining-baskets-skips";

    public string Title => "剩下的不装记录";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "remaining-baskets-skips";

    public string RenderFact(JsonElement fact) => "";
}
