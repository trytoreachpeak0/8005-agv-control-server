using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7MultiDemandAdvanceTests;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.UnloadCrashResumeTests;

namespace ControlServer.Tests;

/// <summary>
/// 已出结果的 SlotOperationCommand 不再补发（control-server#623）。
/// </summary>
/// <remarks>
/// 生产形状（agv01 10-22 attempt 3f01992d）：装货结果回 RECOVERY_REQUIRED，旅程阻塞；补偿终结那一条、cs#499 把旅程放回之后，
/// 引擎每轮都把原装货命令补发一次，车载端每次当重复命令忽略、不回任何东西，行永不确认——换协议身份时
/// <c>ProtocolOutboxIdentityStartupCheck</c> 拒绝启动。原因：命令只在 Committed 与确定的失败两处结算，RecoveryRequired 之后补偿放回走
/// <c>resumingAfterCommit</c>，跳过结算。
/// </remarks>
public sealed partial class RecoveryEndingReleasesBlockedJourneyTests
{
    /// <summary>
    /// 3f01992d：一站两条，第二条装货结果要恢复、补偿掉，旅程放回。之后几轮都不再发第二条的装货命令，它的发件箱行已不在待补发之列。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensatedLoadsCommandIsNotReplayedAfterTheJourneyIsReleased()
    {
        await WithProofAsync(async () =>
        {
            await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            await LoadTheFirstAndBlockOnTheSecondAtOnePickupAsync(fixture);
            string command = (await MembershipAsync(fixture, SecondDemandId)).LoadCommandMessageId;

            await CompensateAsync(fixture, SecondDemandId);
            await fixture.RestoreSessionReadyAsync();
            int sentBefore = fixture.Peer.Lines.Count;
            Exception? first = await RunRoundAsync(fixture);
            Exception? second = await RunRoundAsync(fixture);
            Exception? third = await RunRoundAsync(fixture);

            int resent = SentSince(fixture, sentBefore, command);
            string[] pending = await fixture.PendingOutboxMessageIdsAsync();
            Assert.True(
                resent == 0 && !pending.Contains(command),
                $"The compensated load's SlotOperationCommand {command} was replayed {resent} time(s) after the release " +
                $"and is still pending={pending.Contains(command)}. first={first?.Message} second={second?.Message} third={third?.Message}");
        });
    }

    /// <summary>每轮发给车的线里，messageId 为 <paramref name="messageId"/> 的条数（只数 <paramref name="from"/> 之后的）。</summary>
    internal static int SentSince(RuntimeFixture fixture, int from, string messageId) =>
        fixture.Peer.Lines.Skip(from).Count(line =>
        {
            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(line));
            return document.RootElement.GetProperty("messageId").GetString() == messageId;
        });
}
