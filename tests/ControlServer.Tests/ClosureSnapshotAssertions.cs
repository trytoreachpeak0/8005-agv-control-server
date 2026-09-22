using System.Text.Json;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 旅程收尾之后车上看得见的那三张快照（control-server#323）：空清单、空计划、不带旅程的业务状态。
/// </summary>
/// <remarks>
/// <para>
/// <b>只按车能看到的东西判，不按实现怎么算。</b>收尾快照认的是内容——清单 <c>items</c> 为空、计划 <c>legs</c> 为空、业务状态
/// <c>activePurpose</c> 为 null——而不是某个 messageId 或某条算式给出的号。号只断一件事：比这辆车这条流上<b>别的每一版</b>都大。
/// 那是车载端采纳它的条件（按消息类型记修订号，回退即 <c>SNAPSHOT_REVISION_REGRESSION</c>），与服务端用哪条算式无关。
/// </para>
/// <para>
/// <b>「发出」断的是线上那一行，不是发件箱那一行。</b>修前的样子正是「该有的都在库里、车一行也没收到」的反面——库里就没有；
/// 但只断库会放过「暂存了却没人发」，而那是收尾放在事务里、发送放在事务外之后最容易漏的一步。
/// </para>
/// </remarks>
internal static class ClosureSnapshotAssertions
{
    internal sealed record Snapshot(string MessageType, string MessageId, long Revision, JsonElement Payload);

    /// <summary>
    /// 这辆车恰好有一组收尾快照，形状正确、号在各自那条流上最大，而且三张都以 <paramref name="sessionGeneration"/> 发到了线上。
    /// </summary>
    internal static async Task<IReadOnlyList<Snapshot>> AssertClosureSentAsync(
        ControlServerDbContext context,
        string agvId,
        IEnumerable<string> sentLines,
        long sessionGeneration,
        string expectedStationId)
    {
        IReadOnlyList<Snapshot> closure = await AssertClosureStagedAsync(context, agvId, expectedStationId);
        string[] lines = [.. sentLines];
        foreach (Snapshot snapshot in closure)
        {
            Assert.True(
                lines.Any(line => SentAs(line, snapshot.MessageId, sessionGeneration)),
                $"The closure {snapshot.MessageType} {snapshot.MessageId} never went on the wire in generation {sessionGeneration}.");
        }

        return closure;
    }

    /// <summary>同上，但只断发件箱：用于「当场没发出去、等重连补发」的用例。</summary>
    internal static async Task<IReadOnlyList<Snapshot>> AssertClosureStagedAsync(
        ControlServerDbContext context,
        string agvId,
        string expectedStationId)
    {
        Snapshot[] all = await SnapshotsAsync(context, agvId);

        Snapshot worklist = SingleClosure(all, "CurrentStopWorklistSnapshot",
            payload => payload.GetProperty("items").GetArrayLength() == 0);
        Assert.Equal(expectedStationId, worklist.Payload.GetProperty("stationId").GetString());
        Assert.Equal(JsonValueKind.Null, worklist.Payload.GetProperty("stationDepartureDeadlineAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, worklist.Payload.GetProperty("operationSessionId").ValueKind);

        Snapshot plan = SingleClosure(all, "UpcomingStopPlanSnapshot",
            payload => payload.GetProperty("legs").GetArrayLength() == 0);

        Snapshot business = SingleClosure(all, "VehicleBusinessStateSnapshot",
            payload => payload.GetProperty("activePurpose").ValueKind == JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, business.Payload.GetProperty("loadingPhase").ValueKind);

        return [worklist, plan, business];
    }

    /// <summary>这辆车发件箱里某一类快照，连同修订号与载荷。</summary>
    internal static async Task<Snapshot[]> SnapshotsAsync(ControlServerDbContext context, string agvId)
    {
        ProtocolOutboxRow[] rows = await context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot" ||
                          row.MessageType == "UpcomingStopPlanSnapshot" ||
                          row.MessageType == "VehicleBusinessStateSnapshot")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        List<Snapshot> snapshots = [];
        foreach (ProtocolOutboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement root = document.RootElement;
            if (root.GetProperty("agvId").GetString() != agvId)
            {
                continue;
            }

            JsonElement payload = root.GetProperty("payload").Clone();
            snapshots.Add(new Snapshot(row.MessageType, row.MessageId, payload.GetProperty(RevisionProperty(row.MessageType)).GetInt64(), payload));
        }

        return [.. snapshots];
    }

    /// <summary>这条线是不是以这个 messageId、这一代会话发出去的。</summary>
    internal static bool SentAs(string line, string messageId, long sessionGeneration)
    {
        using JsonDocument document = JsonDocument.Parse(line.TrimEnd('\n'));
        JsonElement root = document.RootElement;
        return root.GetProperty("messageId").GetString() == messageId &&
               root.GetProperty("sessionGeneration").GetInt64() == sessionGeneration;
    }

    internal static string RevisionProperty(string messageType) => messageType switch
    {
        "CurrentStopWorklistSnapshot" => "worklistRevision",
        "UpcomingStopPlanSnapshot" => "planRevision",
        "VehicleBusinessStateSnapshot" => "vehicleBusinessStateRevision",
        _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null)
    };

    private static Snapshot SingleClosure(Snapshot[] all, string messageType, Func<JsonElement, bool> isClosure)
    {
        Snapshot[] ofType = [.. all.Where(item => item.MessageType == messageType)];
        Snapshot closure = Assert.Single(ofType, item => isClosure(item.Payload));
        foreach (Snapshot other in ofType.Where(item => item.MessageId != closure.MessageId))
        {
            Assert.True(
                closure.Revision > other.Revision,
                $"The closure {messageType} is revision {closure.Revision}, not above {other.MessageId} at {other.Revision}.");
        }

        return closure;
    }
}
