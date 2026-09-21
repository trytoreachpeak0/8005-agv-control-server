namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 防饥饿阈值标定证据的只读查询端点（REQ-0203；批次7-09，control-server#214）。
/// </summary>
public static class StarvationCalibrationEndpoints
{
    public const string Route = "/api/dispatch/starvation-calibration";

    public static IEndpointRouteBuilder MapStarvationCalibrationReport(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
