using System.Text.Json;
using System.Text.RegularExpressions;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 站点任务清单每条 item 的机台号 <c>eqp</c>（协议 7.0.0 候选，protocol#29；control-server#896）：服务端原样复制受理时记下的
/// MES <c>EQP</c>，没有就发 <c>null</c>，不拿它判断任何事（<c>CV-WORKLIST-EQP-DISPLAY-ONLY</c>，<c>FP-IS-09</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>取的是受理时那一份，不是现读的目录。</b>每条用例都在受理之后、车到站之前把需求行上记下的 EQP 换成一个目录里没有的值，
/// 目录本身仍是 <c>EQP-01</c>。清单若现读目录，或者从别处拼出机台号，显示的就是 <c>EQP-01</c>，用例红。这也正是票面要的：
/// 操作员眼前那一行不会因为 MES 后来改了机台号而在手里变掉。
/// </para>
/// <para>
/// <b>「不据此判断」按整趟旅程对外发出的东西比。</b>同一趟旅程跑两遍，一遍有 EQP、一遍没有：除了清单条目里 <c>eqp</c> 这一个值，
/// 发件箱每一条报文的 id、类型、关联 id、修订号与载荷，线上发出的顺序，RIoT 订单，以及旅程的终态都逐字相同。服务端拿它做了任何
/// 决定（挡清单、改派、改修订号、改命令），这里都会有一行不同。
/// </para>
/// </remarks>
public sealed partial class WorklistEqpDisplayOnlyTests
{
    private const string AcceptedEqp = "N1-03 机台 07";
    private const string ThirdDemandId = "10000000-0000-4000-8000-000000000003";
    private const string ThirdSublot = "SUBLOT-003";
    private const string FourthDemandId = "10000000-0000-4000-8000-000000000004";
    private const string FourthSublot = "SUBLOT-004";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 一趟机台取货旅程，取货停靠与卸货停靠两张有项的清单，每条 item 的 <c>eqp</c> 都是受理时记下的那个值，逐字：
    /// 空格与中文都不动。
    /// </summary>
    /// <remarks>
    /// 后两个取值是审查 L1 补的：只在中间有空格的值测不到修剪，把 <c>eqp</c> 修剪掉、或把只有空白的值当成 null 的实现
    /// 都能放过第一个取值。票意是原样复制，协议只拒空串（<c>minLength: 1</c>），首尾空白与全空白都照原样发出。
    /// </remarks>
    [Theory]
    [InlineData(AcceptedEqp)]
    [InlineData("  N1-03 机台 07\t ")]
    [InlineData("   ")]
    [Trait("IntegrationSlice", "FP-IS-09")]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task EveryWorklistItemCarriesTheEqpRecordedAtAcceptanceVerbatim(string acceptedEqp)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        JourneyRuntimeRow runtime = await RunJourneyWithAcceptedEqpAsync(fixture, acceptedEqp);
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);

        JsonElement[] items = await WorklistItemsAsync(fixture);
        Assert.Equal(["DROPOFF", "PICKUP"], items.Select(item => item.GetProperty("stopRole").GetString()).Order(StringComparer.Ordinal));
        Assert.All(items, item =>
        {
            Assert.Equal(FirstDemandId, item.GetProperty("demandId").GetString());
            Assert.Equal(JsonValueKind.String, item.GetProperty("eqp").ValueKind);
            Assert.Equal(acceptedEqp, item.GetProperty("eqp").GetString());
        });
    }

    /// <summary>
    /// 同一个取货停靠上四条需求：锚需求有 EQP，另三条分别是 EQP 为 null、为空串、整组 MES 字段都没有。清单照常发、四条都列出，
    /// 后三条的 <c>eqp</c> 是显式的 <c>null</c>（字段在，不省略；协议不收空串），录入请求照样期待四个子批，旅程停在等录入、不挂阻塞。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-09")]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task ADemandWithoutAnEqpIsListedWithAnExplicitNullAndBlocksNothing()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        // 后三条是锚需求那一行的克隆（含 MES 字段），所以先加、再各自改，锚需求最后改。
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, runtime, SecondDemandId, SecondSublot, slotBlock: 1);
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, runtime, ThirdDemandId, ThirdSublot, slotBlock: 2);
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(fixture, runtime, FourthDemandId, FourthSublot, slotBlock: 3);
        await SetAcceptedEqpAsync(fixture, FirstDemandId, AcceptedEqp);
        await SetAcceptedEqpAsync(fixture, SecondDemandId, null);
        await SetAcceptedEqpAsync(fixture, ThirdDemandId, string.Empty);
        await SetAcceptedMesFieldsJsonAsync(fixture, FourthDemandId, "null");

        runtime = await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");

        JsonElement worklist = await OutboundPayloadAsync(fixture, runtime.WorklistMessageId);
        Dictionary<string, JsonElement> byDemand = worklist.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("demandId").GetString()!, item => item.GetProperty("eqp").Clone(), StringComparer.Ordinal);
        Assert.Equal([FirstDemandId, SecondDemandId, ThirdDemandId, FourthDemandId], byDemand.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(AcceptedEqp, byDemand[FirstDemandId].GetString());
        Assert.Equal(JsonValueKind.Null, byDemand[SecondDemandId].ValueKind);
        Assert.Equal(JsonValueKind.Null, byDemand[ThirdDemandId].ValueKind);
        Assert.Equal(JsonValueKind.Null, byDemand[FourthDemandId].ValueKind);

        JsonElement request = await OutboundPayloadAsync(fixture, runtime.SublotRequestMessageId!);
        Assert.Equal(
            [FirstSublot, SecondSublot, ThirdSublot, FourthSublot],
            request.GetProperty("expectedSublots").EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        Assert.Null(runtime.BlockReasonCode);
    }

    /// <summary>
    /// 同一趟旅程跑两遍，受理时记下的 EQP 一遍是 <see cref="AcceptedEqp"/>、一遍是 null：抹掉清单条目里 <c>eqp</c> 的值之后，
    /// 发件箱、线上与 RIoT 订单逐字相同，两趟都正常完成。服务端没有拿它决定任何事。
    /// </summary>
    /// <remarks>
    /// 先断言两份原样记录确实不同，否则「抹掉之后相同」可能只是 <c>eqp</c> 根本没上线。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-09")]
    [Trait("ProtocolVector", "CV-WORKLIST-EQP-DISPLAY-ONLY")]
    public async Task TheServerDecidesNothingByTheWorklistEqp()
    {
        await using RuntimeFixture withEqp = await RuntimeFixture.CreateAsync();
        await using RuntimeFixture withoutEqp = await RuntimeFixture.CreateAsync();

        Assert.Equal(JourneyRuntimeStage.Completed, (await RunJourneyWithAcceptedEqpAsync(withEqp, AcceptedEqp)).Stage);
        Assert.Equal(JourneyRuntimeStage.Completed, (await RunJourneyWithAcceptedEqpAsync(withoutEqp, null)).Stage);

        string recordedWith = await WirePin.CaptureAsync(withEqp.Context, withEqp.Peer.Lines);
        string recordedWithout = await WirePin.CaptureAsync(withoutEqp.Context, withoutEqp.Peer.Lines);
        Assert.NotEqual(recordedWith, recordedWithout);
        Assert.Contains("\"eqp\":null", recordedWithout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"eqp\":null", recordedWith, StringComparison.Ordinal);

        Assert.Equal(WithoutEqpValues(recordedWithout), WithoutEqpValues(recordedWith));
    }

    /// <summary>
    /// 受理、派车之后、车到取货站之前，把需求行上受理时记下的 EQP 换成 <paramref name="eqp"/>，然后整趟跑完。
    /// </summary>
    private static async Task<JourneyRuntimeRow> RunJourneyWithAcceptedEqpAsync(RuntimeFixture fixture, string? eqp)
    {
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await TickAndRunAsync(fixture);
        await SetAcceptedEqpAsync(fixture, FirstDemandId, eqp);

        await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        return await ArriveAtGateAndUnloadAsync(fixture, FirstDemandId);
    }

    private static async Task SetAcceptedEqpAsync(RuntimeFixture fixture, string demandId, string? eqp)
    {
        AcceptedDemandRow row = await fixture.Context.AcceptedDemands.SingleAsync(item => item.DemandId == demandId, Token);
        LiveMesFieldSet fields = JsonSerializer.Deserialize<LiveMesFieldSet>(row.LiveMesFieldsJson)!;
        Assert.Equal("EQP-01", fields.Eqp);
        row.LiveMesFieldsJson = JsonSerializer.Serialize(fields with { Eqp = eqp });
        await fixture.Context.SaveChangesAsync(Token);
    }

    private static async Task SetAcceptedMesFieldsJsonAsync(RuntimeFixture fixture, string demandId, string json)
    {
        AcceptedDemandRow row = await fixture.Context.AcceptedDemands.SingleAsync(item => item.DemandId == demandId, Token);
        row.LiveMesFieldsJson = json;
        await fixture.Context.SaveChangesAsync(Token);
    }

    /// <summary>发件箱里每一张有项的清单的全部 item。</summary>
    private static async Task<JsonElement[]> WorklistItemsAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        List<JsonElement> items = [];
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            items.AddRange(document.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray()
                .Select(item => item.Clone()));
        }

        Assert.NotEmpty(items);
        return [.. items];
    }

    /// <summary>
    /// 把 <see cref="WirePin.CaptureAsync"/> 记录里 <c>eqp</c> 的值与各条载荷摘要抹掉。摘要是对含 <c>eqp</c> 的载荷算的，
    /// 不抹就比不了；载荷全文仍在记录里逐字参与比较，所以抹摘要不放过任何别的差异。
    /// </summary>
    private static string WithoutEqpValues(string recorded) =>
        PayloadDigest().Replace(EqpValue().Replace(recorded, "\"eqp\":<eqp>"), "payloadSha256=<sha>");

    [GeneratedRegex("\"eqp\":(?:null|\"(?:[^\"\\\\]|\\\\.)*\")")]
    private static partial Regex EqpValue();

    [GeneratedRegex("payloadSha256=[0-9a-f]{64}")]
    private static partial Regex PayloadDigest();
}
