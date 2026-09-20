using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-03（control-server#208）的主证据：一趟旅程对外发出去的全部东西，逐字——发件箱每一条报文的 messageId、消息类型、
/// 关联 id、会话代次、修订号与载荷，加上 RIoT 订单的 <c>UpperId</c> 序列。
/// </summary>
/// <remarks>
/// <para>
/// 本票把推进从「按单一需求推进」改成「由当前停靠驱动」：清单、录入请求、计划、装卸命令改从停靠表与从属需求表生成，
/// 修订号改取按车计数器。这是行为不变的预重构，判据只有一个——<b>单需求旅程两个停靠发出去的每一条报文与今天逐字相同</b>。
/// <see cref="ZeroChangePin"/> 钉的是库里落下的状态，钉不到载荷与修订号；这里钉的正是那两样。
/// </para>
/// <para>
/// <b>期望值取自集成分支，不在本票分支上录。</b>初版取自 <c>fp/v2-impl@13a1db75</c>（批次7-02 合入后的顶端，
/// 也是本票分支的起点）——录的那一刻工作树上只有测试文件，一行产品代码都没改，所以它与在集成分支的临时工作树上录逐字等价。
/// 集成分支前移后若要重录，照 <see cref="ZeroChangePin"/> 的类注释做：把测试文件原样搬到集成分支的工作树上跑出来再搬回来。
/// <b>红了就是行为变了，不能重录来变绿。</b>
/// </para>
/// <para>
/// <b>每条消息存的是规范化后的载荷全文，不只是摘要。</b>摘要能判等，判不出差在哪；审查要能独立复核「哪一个 id、哪一个修订号
/// 变了」，就得看得见。规范化只做一件事：递归按键名排序，让 JSON 属性顺序的变动不算差异。
/// </para>
/// <para>
/// <b>默认一个值都不遮。</b>固定时钟与写死的入站 id 之下，记下来的每一个值都可重现：出站 id 全部派生自需求 id，
/// 载荷里的时刻由固定时钟决定。唯一的例外走 <c>volatileValues</c>，见它的说明——那是给复用既有驱动的路径留的口子，
/// 因为那些测试用 <c>Guid.NewGuid()</c> 造入站 id。哪一个没遮的值飘了，都是被钉住的事实变了。
/// </para>
/// </remarks>
internal static class WirePin
{
    /// <param name="volatileValues">
    /// 本轮测试自己随机生成、因而每跑一次都不同的值，按给出的顺序记成 <c>&lt;volatile-1&gt;</c>、<c>&lt;volatile-2&gt;</c>……
    /// 只有一类值需要它：测试用 <c>Guid.NewGuid()</c> 造的入站 messageId，它会原样进装货命令的 <c>correlationId</c>。
    /// 遮的是「这个值是随机的」，遮不掉「它出现在哪里、出现几次」——位置与重复仍然被钉住。
    /// <b>不要拿它遮一个本该确定的值</b>：那等于把对照改小到刚好放过自己。
    /// </param>
    /// <param name="sentLines">
    /// <c>RecordingPeer.Lines</c>：真正写到线上的每一行，按发送先后。发件箱表记不住这件事——重连补发是把同一行
    /// 换个会话代次再发一遍，表里只剩最后一次的样子，而「补发了哪几条、按什么顺序」正是本票要钉的。给了就多记一节。
    /// </param>
    internal static async Task AssertMatchesAsync(
        ControlServerDbContext context,
        string pinName,
        IReadOnlyList<byte[]>? sentLines = null,
        IReadOnlyList<string>? volatileValues = null,
        [CallerFilePath] string sourceFile = "")
    {
        string actual = (await CaptureAsync(context, sentLines, volatileValues)).ReplaceLineEndings("\n");
        string path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "WirePins", pinName + ".txt");
        // 实测值总是落到旁边的 .actual 文件（已 gitignore），好让一次失败能直接 diff，也让首次录制有处可取。
        // 它永远不覆盖 pin 本身：重录是删掉 pin、在集成分支的工作树上跑、再把 .actual 改名，是刻意的手工动作。
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".actual", actual, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path), $"Wire pin '{pinName}' has no expected state at {path}; actual is at {path}.actual");
        Assert.Equal((await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n"), actual);
    }

    /// <summary>
    /// 发件箱按 <c>CreatedAt</c> 排，同一时刻的再按 <c>MessageId</c> 序数排。固定时钟下同一轮发出的几条时刻相同，
    /// 靠 id 定序；<c>CreatedAt</c> 在前，是为了让「跨轮次的先后」这个事实本身也被钉住。
    /// </summary>
    internal static async Task<string> CaptureAsync(
        ControlServerDbContext context,
        IReadOnlyList<byte[]>? sentLines = null,
        IReadOnlyList<string>? volatileValues = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        // 遮罩在算摘要之前做，不是在成文之后：摘要是对记下来的那段文本算的，两者必须是同一段文本。
        string Mask(string text)
        {
            for (int slot = 0; slot < (volatileValues?.Count ?? 0); slot++)
            {
                text = text.Replace(volatileValues![slot], $"<volatile-{slot + 1}>", StringComparison.Ordinal);
            }

            return text;
        }

        List<string> lines = ["## Outbound (CreatedAt, then MessageId)"];
        ProtocolOutboxRow[] outbox = await context.ProtocolOutbox.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        int index = 0;
        foreach (ProtocolOutboxRow row in outbox
                     .OrderBy(item => item.CreatedAt)
                     .ThenBy(item => item.MessageId, StringComparer.Ordinal))
        {
            using JsonDocument envelope = JsonDocument.Parse(row.PayloadJson);
            JsonElement root = envelope.RootElement;
            JsonElement payload = root.GetProperty("payload");
            string canonical = Mask(Canonical(payload));
            string? revisionProperty = JourneyRuntimeWorkerTestKit.SnapshotRevisionProperty(row.MessageType);
            string revision = revisionProperty is not null && payload.TryGetProperty(revisionProperty, out JsonElement value)
                ? value.GetRawText()
                : "-";
            lines.Add(
                $"{++index:D2} t={Elapsed(row.CreatedAt)} type={row.MessageType} messageId={row.MessageId} " +
                $"correlationId={Mask(Text(root, "correlationId"))} " +
                $"sessionGeneration={root.GetProperty("sessionGeneration").GetRawText()} " +
                $"revision={revision} acknowledged={(row.AcknowledgedAt is null ? "no" : "yes")} " +
                $"fenced={(row.FencedAt is null ? "no" : "yes")} payloadSha256={Sha256(canonical)}");
            lines.Add($"   payload={canonical}");
        }

        if (sentLines is not null)
        {
            lines.Add("## Wire (as sent, in order)");
            index = 0;
            foreach (byte[] line in sentLines)
            {
                using JsonDocument envelope = JsonDocument.Parse(Encoding.UTF8.GetString(line));
                JsonElement root = envelope.RootElement;
                JsonElement payload = root.GetProperty("payload");
                string messageType = root.GetProperty("messageType").GetString() ?? "?";
                string? revisionProperty = JourneyRuntimeWorkerTestKit.SnapshotRevisionProperty(messageType);
                string revision =
                    revisionProperty is not null && payload.TryGetProperty(revisionProperty, out JsonElement value)
                        ? value.GetRawText()
                        : "-";
                lines.Add(
                    $"{++index:D2} type={messageType} messageId={Mask(Text(root, "messageId"))} " +
                    $"correlationId={Mask(Text(root, "correlationId"))} " +
                    $"sessionGeneration={root.GetProperty("sessionGeneration").GetRawText()} " +
                    $"revision={revision} payloadSha256={Sha256(Mask(Canonical(payload)))}");
            }
        }

        lines.Add("## RIoT orders (CreatedAt, then UpperId)");
        OrderIntentRow[] orders = await context.OrderIntents.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        index = 0;
        foreach (OrderIntentRow order in orders
                     .OrderBy(item => item.CreatedAt)
                     .ThenBy(item => item.UpperId, StringComparer.Ordinal))
        {
            lines.Add(
                $"{++index:D2} t={Elapsed(order.CreatedAt)} purpose={order.Purpose} upperId={order.UpperId} status={order.Status} " +
                $"movementLegId={order.MovementLegId} demandId={order.DemandId} " +
                $"targetStationId={order.TargetStationId} destinationStationId={order.DestinationStationId} " +
                $"dispatchGeneration={order.DispatchGeneration}");
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// 相对固定时钟起点的秒数。测试每推进一轮把钟往前拨一秒，所以这一栏就是「第几轮发出去的」——排序键的前一半，
    /// 也是唯一能把「发送先后」这个事实钉进 pin 的东西：绝对时刻同一秒的几条只能靠 id 定序，那不是发送顺序。
    /// </summary>
    private static string Elapsed(DateTimeOffset at) =>
        $"+{(at - JourneyRuntimeWorkerTestKit.Now).TotalSeconds:0.###}s";

    private static string Text(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind is not JsonValueKind.Null
            ? value.GetString() ?? "NULL"
            : "NULL";

    /// <summary>递归按键名序数排序；数组保序，因为顺序在协议里是事实。</summary>
    private static string Canonical(JsonElement element) =>
        Sort(JsonNode.Parse(element.GetRawText()))?.ToJsonString(JsonSerializerOptions.Default) ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o
            .OrderBy(property => property.Key, StringComparer.Ordinal)
            .Select(property => KeyValuePair.Create(property.Key, Sort(property.Value?.DeepClone())))),
        JsonArray a => new JsonArray([.. a.Select(item => Sort(item?.DeepClone()))]),
        _ => node?.DeepClone()
    };

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
