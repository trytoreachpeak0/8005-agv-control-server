using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.FieldOps;

/// <summary>
/// 接受地图新名称为基线（control-server#186）：Map 级改名恢复的第一步。之后再用 <c>release-task-type-station-hold</c>
/// 逐个任务类型解除改名带来的暂停；改名没被接受之前，那个动词拒绝解除。
/// </summary>
/// <remarks>
/// 本工具够不到 RIoT：<c>--map-name</c> 由现场照 RIoT 上看到的名称敲进来，必须与服务端读到、尚未接受的那个名称逐字节相同，
/// 否则拒绝并写审计——敲错一个字不会变成一条错的基线。
/// </remarks>
internal static partial class Program
{
    private const string AcceptMapNameCommand = "accept-map-name";

    private static async Task<int> AcceptMapNameAsync(
        ControlServerDbContext context,
        GovernanceStore governance,
        Dictionary<string, string> options,
        DateTimeOffset now)
    {
        if (!TryReadMap(options, out int mapId) || !options.TryGetValue("map-name", out string? mapName))
        {
            return Usage($"{AcceptMapNameCommand} needs --map <id> --map-name <name> --reason <text>");
        }
        if (!TryReadRequest(options, AcceptMapNameCommand, out TaskTypeStationChangeRequest? request, out int usage))
        {
            return usage;
        }

        MapNameBaselineAcceptResult result = await new MapNameBaselineAcceptanceService(
                new MapNameBaselineStore(context, governance), governance)
            .AcceptAsync(mapId, mapName, request!, now, CancellationToken.None);
        return Emit(
            new
            {
                command = AcceptMapNameCommand,
                outcome = result.Accepted ? "OK" : "REJECTED",
                mapId = result.MapId,
                previousName = result.PreviousName,
                acceptedName = result.AcceptedName,
                violations = Violations(result.Violations),
                auditRecordId = result.AuditRecordId
            },
            result.Accepted ? 0 : 1);
    }
}
