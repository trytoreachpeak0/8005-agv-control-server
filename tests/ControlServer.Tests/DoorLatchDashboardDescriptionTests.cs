using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Faults;

namespace ControlServer.Tests;

/// <summary>
/// 看板上门锁类急停的说明按现行行为写（control-server#788）。CP-0011（control-server#539，2026-10-13）之后，行驶中门锁类急停
/// 不自动解除，门锁读回锁闭也不解除：要人确认门锁已检修、经服务端人工确认解除，再经故障处置「继续原单」或清除故障。三份描述表
/// （搬运 <see cref="BlockedJourneysQueryEndpoint.Descriptions"/>、空闲返回 <see cref="IdleReturnCodeDescriptions.SharedCodesOnIdleReturnJourneys"/>、
/// 充电 <see cref="ChargingDashboardDescriptions.SharedCodesOnChargingJourneys"/>）却还写着修订前的「门锁恢复后急停自动解除」，
/// 也没写不要在 RIoT 上解锁。2026-10-19 agv01 现场照着等「锁好后自动解除」，等不到就去 RIoT 上解了两次，两次都被判为外部解除、
/// 立即重新急停（<c>EMERGENCY_LATCH_RELEASED_EXTERNALLY</c>），最后车退出服务。
/// </summary>
/// <remarks>
/// 只断言关键短语，不逐字钉全文，免得以后每改一个字都要改测试。
/// </remarks>
public sealed class DoorLatchDashboardDescriptionTests
{
    private static readonly string[] DoorCodes =
    [
        VehicleFaultEvidence.DoorNotProvenLocked,
        JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason,
    ];

    private static readonly (string Table, IReadOnlyDictionary<string, string> Descriptions)[] Tables =
    [
        ("搬运", BlockedJourneysQueryEndpoint.Descriptions),
        ("空闲返回", IdleReturnCodeDescriptions.SharedCodesOnIdleReturnJourneys),
        ("充电", ChargingDashboardDescriptions.SharedCodesOnChargingJourneys),
    ];

    public static TheoryData<string, string> EveryTableAndDoorCode()
    {
        TheoryData<string, string> data = [];
        foreach ((string table, _) in Tables)
        {
            foreach (string code in DoorCodes)
            {
                data.Add(table, code);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTableAndDoorCode))]
    public void ADoorCodeSaysNotToUnlockOnRiotAndNamesThePersonsWayOut(string table, string code)
    {
        string text = Describe(table, code);

        Assert.DoesNotContain("自动解除", text, StringComparison.Ordinal);
        Assert.Contains("不要在 RIoT 上解锁", text, StringComparison.Ordinal);
        Assert.Contains("立即重新急停", text, StringComparison.Ordinal);
        Assert.Contains("联系系统维护人员", text, StringComparison.Ordinal);
        Assert.Contains("人工确认解除", text, StringComparison.Ordinal);
        Assert.Contains("「继续原单」", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryTableAndDoorCode))]
    public void ADoorCodeCarriesNoTicketNumberAndNoCoordinator(string table, string code)
    {
        string text = Describe(table, code);

        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
        Assert.DoesNotContain("REQ-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CP-", text, StringComparison.Ordinal);
        Assert.DoesNotContain("调度", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".md", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("搬运")]
    [InlineData("空闲返回")]
    [InlineData("充电")]
    public void TheDoorFaultSaysThatTheLockReadingLockedReleasesNothingAndHowToGetPastAPressedButton(string table)
    {
        string text = Describe(table, VehicleFaultEvidence.DoorNotProvenLocked);

        Assert.Contains("门锁读回锁闭，服务端也不会解除", text, StringComparison.Ordinal);
        Assert.Contains("确认门锁已检修", text, StringComparison.Ordinal);
        Assert.Contains("清除故障", text, StringComparison.Ordinal);
        Assert.Contains("先在车上松开按钮", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("搬运")]
    [InlineData("空闲返回")]
    [InlineData("充电")]
    public void AHeldOrderResumedWithoutContinueFollowsAPersonsReleaseNotAnAutomaticOne(string table)
    {
        string text = Describe(table, JourneyRuntimeEngine.HeldOrderResumedWithoutContinueReason);

        Assert.Contains("人工确认解除急停之后", text, StringComparison.Ordinal);
        Assert.Contains("RIoT 却报它在执行", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 守卫：三份描述表里凡是讲门锁与急停的说明，都不出现「自动解除」。只看两个码挡不住下一个新加的门锁类码照抄旧句。
    /// </summary>
    [Fact]
    public void NoDescriptionThatTalksAboutTheDoorsAndTheEmergencyStopSaysItReleasesAutomatically()
    {
        string[] offending =
        [
            .. Tables.SelectMany(table => table.Descriptions
                .Where(entry => entry.Value.Contains("门锁", StringComparison.Ordinal)
                    && entry.Value.Contains("急停", StringComparison.Ordinal)
                    && entry.Value.Contains("自动解除", StringComparison.Ordinal))
                .Select(entry => $"{table.Table}:{entry.Key}")),
        ];

        Assert.Empty(offending);
    }

    /// <summary>三份表里同一个门锁类码，出路那一段逐字一致：差别只在开往哪里、保持什么。</summary>
    [Theory]
    [InlineData("不要在 RIoT 上解锁")]
    [InlineData("联系系统维护人员")]
    public void TheThreeTablesGiveTheSameWayOutForTheDoorFault(string from)
    {
        string[] tails =
        [
            .. Tables.Select(table => Tail(table.Descriptions[VehicleFaultEvidence.DoorNotProvenLocked], from)),
        ];

        Assert.All(tails, tail => Assert.Equal(tails[0], tail));
    }

    private static string Describe(string table, string code)
    {
        IReadOnlyDictionary<string, string> descriptions = Tables.Single(entry => entry.Table == table).Descriptions;
        Assert.True(descriptions.TryGetValue(code, out string? text), $"{table} has no description for {code}");
        return text!;
    }

    private static string Tail(string text, string from)
    {
        int index = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{from}' not in: {text}");
        return text[index..];
    }
}
