using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.FieldOps;

/// <summary>
/// 发件箱残留体检（control-server#623）：未确认、未退役，而所属旅程已收尾、所属需求已终结或已移出旅程、或装卸命令已有结论的行。
/// **只读，一行不写**：以 SQLite 只读模式开库，不拿库锁，服务端还跑着时也能先看一眼。
/// </summary>
/// <remarks>
/// <para>
/// 部署换协议身份之前用：这些行车不会再确认，留着就让 <c>ProtocolOutboxIdentityStartupCheck</c>（<c>OUTBOX_PROTOCOL_IDENTITY_MISMATCH</c>）
/// 拒绝启动。怎么处理（换新库目录、停服退役）是部署时有人负责的决定，这里只列。
/// </para>
/// <para>
/// 退出码沿用本工具的约定：0 是没有残留，1 是查出了残留（不是工具出错），2 是用法错误。<c>pendingCount</c> 是全部未确认行的条数，
/// 残留只是其中一部分；其余行（还活着的旅程在等的）照样会被身份检查拦，但那要等车确认或旅程结束，不是残留。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const string OutboxResidueCommand = "outbox-residue";

    private static async Task<int> OutboxResidueAsync(ControlServerDbContext context)
    {
        IReadOnlyList<OutboxResidueLine> lines = await OutboxResidue.ScanAsync(context, CancellationToken.None);
        OutboxResidueLine[] residue = [.. lines.Where(line => line.Reasons.Length > 0)];
        return Emit(
            new
            {
                command = OutboxResidueCommand,
                outcome = residue.Length == 0 ? "OK" : "FINDINGS",
                pendingCount = lines.Count,
                count = residue.Length,
                byVehicle = residue
                    .GroupBy(line => line.AgvId ?? string.Empty, StringComparer.Ordinal)
                    .Select(group => new { agvId = group.Key, count = group.Count() }),
                findings = residue.Select(line => new
                {
                    messageId = line.MessageId,
                    messageType = line.MessageType,
                    agvId = line.AgvId,
                    createdAt = line.CreatedAt,
                    journeyId = line.JourneyId,
                    journeyStage = line.JourneyStage,
                    demandId = line.DemandId,
                    membershipStatus = line.MembershipStatus,
                    membershipRemoved = line.MembershipRemoved,
                    slotCommandConclusion = line.SlotCommandConclusion,
                    reasons = line.Reasons
                })
            },
            residue.Length == 0 ? 0 : 1);
    }
}
