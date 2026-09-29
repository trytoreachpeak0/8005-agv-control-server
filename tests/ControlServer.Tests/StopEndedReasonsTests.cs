using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// v3 的 <c>CurrentStopWorklistSnapshot.stopEndedReason</c>（control-server#382）：服务端的来路原因码到那七个取值的对照，
/// 以及「有项就是 null、空清单一定有原因」这条 schema 的 <c>if/then/else</c> 在发送前就守住。
/// </summary>
/// <remarks>
/// <para>
/// 对照表来自 <c>8005-agv-program</c> PR #163 正文第 3 项：九条来路、七个原因码（正常卸完为 null）、七个取值。两条来路共用
/// <c>STATION_DEADLINE_EXPIRED</c>（期限到期与确定的装货失败都以 <c>CANCELLED_BY_STATION_TIMEOUT</c> 终结），两种取消共用
/// <c>LOAD_CANCELLED</c>。
/// </para>
/// <para>
/// <b>为什么对未知码抛而不是给个兜底值。</b>空清单的原因是给操作员看的；兜底值会让一条新来路悄悄显示成别的原因。抛出来，那条
/// 新来路的第一条测试就会红，逼着有人回到这张表加一行。
/// </para>
/// </remarks>
public sealed class StopEndedReasonsTests
{
    /// <summary>今天每条让一站结束的来路带的原因码，与它该说的取值。按值断言，一条一条。</summary>
    public static TheoryData<string?, string> EveryProducedReasonCode() => new()
    {
        { null, "COMPLETED" },
        { "CANCELLED_BY_STATION_TIMEOUT", "STATION_DEADLINE_EXPIRED" },
        { "CANCELLED_BY_OPERATOR", "LOAD_CANCELLED" },
        { "CANCELLED_BY_LOAD_COMPENSATION", "LOAD_COMPENSATED" },
        { "TERMINATED_BY_FAULT_CARGO_HANDOFF", "CARGO_HANDED_OFF" },
        { DemandJourneyLookup.ReleasedForRedispatchReason, "DEMAND_RELEASED" },
        { VehicleFaultRecoveryService.TripTerminatedReason, "TRIP_TERMINATED" },
    };

    [Theory]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [MemberData(nameof(EveryProducedReasonCode))]
    public void EachWayAStopCanEndNamesItsOwnReason(string? reasonCode, string expected)
    {
        Assert.Equal(expected, StopEndedReasons.ForEnding(reasonCode));
    }

    /// <summary>
    /// 有值的情形没有一种发 null，而且对照表的值域恰好是 schema 的七值枚举：一个不多（发出去会被拒）、一个不少（有取值没有来路，
    /// 说明哪条来路被错映射了）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void NoEndingIsReportedAsNullAndTheValuesAreExactlyTheSchemaEnumeration()
    {
        string?[] produced = [.. EveryProducedReasonCode().Select(row => (string?)row.Data.Item1)];
        string[] reported = [.. produced.Select(code => StopEndedReasons.ForEnding(code))];

        Assert.All(reported, value => Assert.False(string.IsNullOrEmpty(value)));
        Assert.Equal(
            SchemaEnumeration().Order(StringComparer.Ordinal),
            reported.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void AReasonCodeNobodyMappedIsRefusedRatherThanGuessed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StopEndedReasons.ForEnding("A_CODE_NOBODY_MAPPED"));
    }

    /// <summary>
    /// schema 的条件在发送前守住：空清单不带原因、有项的清单带了原因，都拒绝组装，而不是发一行车载端会按 schema 拒掉的报文。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public void TheWorklistPayloadRefusesAnEmptyListWithoutAReasonAndAListWithItemsWithOne()
    {
        CurrentStopWorklistItem item = new(
            "00000000-0000-4000-8000-000000000001", "KEY-1", "SUBLOT-1", "LOAD", "PICKUP", 7);

        Assert.Throws<InvalidDataException>(() => OnboardJourneyPublisher.ValidateCurrentStopWorklist(
            new CurrentStopWorklistProjection("12", 3, null, null, [], StopEndedReason: null)));
        Assert.Throws<InvalidDataException>(() => OnboardJourneyPublisher.ValidateCurrentStopWorklist(
            new CurrentStopWorklistProjection("12", 3, null, null, [item], StopEndedReason: "COMPLETED")));
        OnboardJourneyPublisher.ValidateCurrentStopWorklist(
            new CurrentStopWorklistProjection("12", 3, null, null, [], StopEndedReason: "COMPLETED"));
        OnboardJourneyPublisher.ValidateCurrentStopWorklist(
            new CurrentStopWorklistProjection("12", 3, null, null, [item], StopEndedReason: null));
    }

    private static string[] SchemaEnumeration()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "vendor", "8005-agv-protocol", "schemas", "messages",
            "CurrentStopWorklistSnapshot.schema.json")));
        JsonElement reason = schema.RootElement.GetProperty("properties").GetProperty("payload")
            .GetProperty("properties").GetProperty("stopEndedReason");
        return
        [
            .. reason.GetProperty("anyOf").EnumerateArray()
                .Where(branch => branch.TryGetProperty("enum", out _))
                .SelectMany(branch => branch.GetProperty("enum").EnumerateArray())
                .Select(value => value.GetString()!)
        ];
    }
}
