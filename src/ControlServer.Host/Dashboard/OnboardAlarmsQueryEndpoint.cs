using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图的告警数据面：每台车要么是当下进看板的告警，要么是拿不到它的原因。
/// </summary>
/// <remarks>
/// 接入这个数据面只加了两个文件——这一个，和看板工程里的那张卡片。**没有改任何一个既有文件**，
/// 这正是 #12 立的自注册约定要经受的第一次真实检验。
/// </remarks>
internal sealed class OnboardAlarmsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "onboard-alarms";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        IReadOnlyList<VehicleAlarmProjection> projections =
            await new OnboardAlarmProjectionStore(dbContext).ReadDashboardProjectionAsync(cancellationToken);
        return projections.Select(projection => new
        {
            agvId = projection.AgvId,
            available = projection.IsAvailable,
            unavailableReason = projection.UnavailableReason,
            snapshotSequence = projection.SnapshotSequence,
            alarms = projection.Alarms.Select(alarm => new
            {
                alarmCode = alarm.AlarmCode,
                severity = alarm.Severity,
                message = alarm.Message
            })
        }).ToArray();
    }
}
