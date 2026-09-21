using System.Text.Json;

namespace ControlServer.Dashboard;

/// <summary>
/// 车队视图的持货等单卡片（批次7-12，control-server#217）。桩：只够测试编译，什么都不写。
/// </summary>
public sealed class CargoHoldingCard : IDashboardCard
{
    public string CardId => "cargo-holding";

    public string Title => "持货等单";

    public DashboardView View => DashboardView.Fleet;

    public string SourcePath => DashboardPaths.QueryPrefix + "cargo-holding";

    public string RenderFact(JsonElement fact) => string.Empty;
}
