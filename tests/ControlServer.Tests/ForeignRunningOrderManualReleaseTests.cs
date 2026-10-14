using System.Net;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 没有 upperId 的外来单（RIoT 网页端手动派的单）结束后读得出结束、车被放开；以及「离开运行列表却读不出结束」的受治理人工出口
/// （control-server#617，切换-14）。
/// </summary>
/// <remarks>
/// <para>
/// 第一组走真实的 <see cref="HttpRiotMovementGateway"/> 与 SDK，替身只在 HTTP 那一层，按现场读到的形态回答（<c>upperId</c> 是空串、
/// <c>orderState=2</c>，<c>evidence/field/20261011-W3-S1-charging/13-foreign-order-readback.txt</c>）：修之前 SDK 把这个答复当成失败，
/// 监看器只看到「读不到」，车永远被按住。<see cref="RecordingRiot"/> 直接实现端口、不经 SDK，测不出这一条。
/// </para>
/// <para>
/// 其余几组是人工出口，判定在 <see cref="ForeignRunningOrderManualRelease"/>，替身用 <see cref="RecordingRiot"/>：它的列表与按单号回读
/// 都可以单独摆布。
/// </para>
/// </remarks>
public sealed class ForeignRunningOrderManualReleaseTests
{
    private const string WebPageOrderId = "order-2109181133127286784";
    private const string Operator = "release-r11";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 修法：按单号回读不再要求 upperId（经真实网关与 SDK） ----------------------------------------------------------------

    /// <summary>
    /// 现场那一张单的复现，修之后的样子：网页手动派的单（upperId 空串）跑在我们车上，被认作外来单、按住车；它结束、离开运行列表后，
    /// 按单号回读得到 <c>orderState=2</c>，记录落 <c>ENDED</c>，车不再被按住，不发任何订单命令。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task AWebPageOrderWithoutAnUpperIdReadsBackEndedThroughTheRealGatewayAndReleasesTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        WebPageRiot riot = new(fixture.Options.VehicleKey);
        await using RiotSession session = riot.Session();
        ForeignRunningOrderSupervisor supervisor = Supervisor(fixture, new HttpRiotMovementGateway(session, fixture.Clock));

        riot.Running = true;
        await supervisor.SuperviseAsync(Token);
        ForeignRiotOrderRow found = await RowAsync(fixture);
        Assert.Equal(
            (ForeignRiotOrderOwnership.Foreign, ForeignRiotOrderStates.HeldCancelNotAuthorized, ""),
            (found.Ownership, found.State, found.UpperId));
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));

        riot.Running = false;
        riot.DetailState = RiotOrderState.Cancelled;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await supervisor.SuperviseAsync(Token);

        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal((ForeignRiotOrderStates.Ended, (int?)RiotOrderState.Cancelled), (row.State, row.EndedOrderState));
        Assert.Empty(await HeldAsync(fixture));
        Assert.Contains("/api/order/v1/orderRecord/detailByOrderId/" + WebPageOrderId, riot.Paths);
        Assert.Empty(fixture.Riot.OrderCommands);
    }

    /// <summary>
    /// 同一条链上的两条反面：单仍在运行列表里时（哪怕按单号能读到终结）不放车；按单号回读真失败（RIoT 回 503）时仍按原规则，过了落定时间
    /// 转 <c>UNSETTLED</c> 交给人，车继续被按住。
    /// </summary>
    [Theory]
    [InlineData("still-running")]
    [InlineData("read-fails")]
    [Trait("Requirement", "REQ-0164")]
    public async Task AWebPageOrderStillRunningOrUnreadableKeepsTheVehicleHeld(string variant)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        WebPageRiot riot = new(fixture.Options.VehicleKey);
        await using RiotSession session = riot.Session();
        ForeignRunningOrderSupervisor supervisor = Supervisor(fixture, new HttpRiotMovementGateway(session, fixture.Clock));
        riot.Running = true;
        await supervisor.SuperviseAsync(Token);

        riot.Running = variant == "still-running";
        riot.DetailState = variant == "still-running" ? RiotOrderState.Cancelled : null;
        for (int round = 0; round < 3; round++)
        {
            fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
            await supervisor.SuperviseAsync(Token);
        }

        Assert.Equal(
            variant == "still-running" ? ForeignRiotOrderStates.HeldCancelNotAuthorized : ForeignRiotOrderStates.Unsettled,
            (await RowAsync(fixture)).State);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        if (variant == "read-fails")
        {
            Assert.Contains("/api/order/v1/orderRecord/detailByOrderId/" + WebPageOrderId, riot.Paths);
        }
    }

    // ---- 人工出口：放开 -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 离开运行列表却读不出结束（<c>UNSETTLED</c>，这里是 RIoT 查不到这张单），具名 R-11 办理：服务端在任何事务之外重读 RIoT，列表读全且没有它、
    /// 按单号回读不是未完成状态，于是放车。记录落 <c>RELEASED_BY_OPERATOR</c>，看板不再列它，写一条成功的管理员审计（对象是这张单），记一条 2233
    /// 告警；之后几轮监看不把它再翻回 <c>UNSETTLED</c>。
    /// </summary>
    [Theory]
    [InlineData("not-found", null)]
    [InlineData("read-ended", RiotOrderState.Deleted)]
    [InlineData("read-throws", null)]
    [Trait("Requirement", "REQ-0164")]
    public async Task AnUnsettledOrderIsReleasedByANamedOperatorOnceRiotNoLongerListsIt(string readBack, int? endedState)
    {
        await using RuntimeFixture fixture = await UnsettledAsync();
        if (readBack == "read-ended")
        {
            fixture.Riot.EndPlacedOrder(WebPageOrderId, RiotOrderState.Deleted);
        }
        List<bool> inTransaction = [];
        fixture.Riot.BeforeListing = _ => inTransaction.Add(fixture.Context.Database.CurrentTransaction is not null);
        RecordingLogger<ForeignRunningOrderManualRelease> log = new();
        using Roster roster = new();
        // Review L3: a by-orderId read that throws (not the gateway's way -- it answers null) is "could not read"; the listing
        // still proves the order is not unfinished.
        IRiotOrderListingFacts? listing = readBack == "read-throws"
            ? new ReadAnswers(fixture.Riot, _ => throw new InvalidOperationException("a defect in the port"))
            : null;

        ForeignRunningOrderManualReleaseResult result = await ReleaseAsync(fixture, roster, Request(fixture), log, listing);
        fixture.Riot.BeforeListing = null;

        Assert.True(result.Released, string.Join(',', result.Codes));
        Assert.Equal([false], inTransaction);
        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal((ForeignRiotOrderStates.ReleasedByOperator, endedState), (row.State, row.EndedOrderState));
        Assert.Equal(fixture.Clock.GetUtcNow(), row.EndedAt);
        Assert.Empty(await HeldAsync(fixture));
        Assert.Null(ForeignRunningOrders.ReasonFor(row.State));
        AdministratorAuditRecordRow audit = Assert.Single(await AuditsAsync(fixture));
        Assert.Equal(
            (result.AuditRecordId, GovernanceActionOutcome.Succeeded, GovernedObjectKind.ForeignRiotOrder, WebPageOrderId, "R-11"),
            (audit.AuditRecordId, audit.Outcome, audit.ObjectKind, audit.ObjectId, audit.ClaimedAdministratorRole));
        using (JsonDocument detail = JsonDocument.Parse(audit.DetailJson!))
        {
            JsonElement root = detail.RootElement;
            Assert.Equal("RELEASED", root.GetProperty("result").GetString());
            Assert.Equal(Operator, root.GetProperty("said").GetProperty("operatorId").GetString());
            Assert.Equal(ForeignRiotOrderStates.Unsettled, root.GetProperty("row").GetProperty("State").GetString());
            Assert.True(root.GetProperty("riot").GetProperty("ListingComplete").GetBoolean());
            Assert.False(root.GetProperty("riot").GetProperty("Listed").GetBoolean());
        }
        (LogLevel level, string message) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(WebPageOrderId, message, StringComparison.Ordinal);
        Assert.Contains(Operator, message, StringComparison.Ordinal);

        for (int round = 0; round < 3; round++)
        {
            fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
            await fixture.Engine.ExecuteOnceAsync(Token);
        }
        Assert.Equal(ForeignRiotOrderStates.ReleasedByOperator, (await RowAsync(fixture)).State);
        Assert.Empty(await HeldAsync(fixture));
        Assert.Empty(fixture.Riot.OrderCommands);
    }

    /// <summary>
    /// 放开之后，RIoT 又把这张单列出来、跑在我们车上：监看照常把它重新认出来、重新按住车（放开不是永久豁免）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task AReleasedOrderThatRunsOnOurVehicleAgainIsHeldAgain()
    {
        await using RuntimeFixture fixture = await UnsettledAsync();
        using Roster roster = new();
        Assert.True((await ReleaseAsync(fixture, roster, Request(fixture))).Released);

        fixture.Riot.PlaceOrder(WebPageOrderId, "", RiotOrderState.Executing, fixture.Options.VehicleKey);
        await fixture.Engine.ExecuteOnceAsync(Token);

        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal((ForeignRiotOrderStates.HeldCancelNotAuthorized, (int?)null, (DateTimeOffset?)null), (row.State, row.EndedOrderState, row.EndedAt));
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
    }

    /// <summary>
    /// control-server#673：被判作没建成的需求腿旧单冒出来、在本车上跑（依据 <c>OWN_DEMAND_LEG_ORDER_ABANDONED</c>，挡车），之后离开运行列表、读不出结束
    /// （<c>UNSETTLED</c>）。具名的人经同一个人工放车入口放开之后，这一行不再挡车，之后几轮监看也不把它重新判成「归属证明不了」——归属只在第一次记这一行时判。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task AnAbandonedDemandLegOrderReleasedByHandNoLongerHoldsTheVehicle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.ForeignOrderCancel.Enabled = false;
        // The rebuild is kept out of this: only the old order's row is under test.
        fixture.Options.OwnOrderRebuildDelay = TimeSpan.FromHours(1);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.Riot.AbsentOrdersReadAsRealRiot = true;
        fixture.Riot.RefuseNextCreateOf = "TO_PICKUP";
        await TickAndRunAsync(fixture);
        string oldUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        foreach (TimeSpan elapsed in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(130) })
        {
            fixture.Clock.Advance(elapsed);
            await fixture.HearFromPeerAsync();
            fixture.Context.ChangeTracker.Clear();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal("OWN_ORDER_NEVER_APPEARED", (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Riot.ReleaseWithheldOrder(oldUpperId);
        string orderId = fixture.Riot.OrderIdOf(oldUpperId)!;
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        ForeignRiotOrderRow held = await OrderRowAsync(fixture, orderId);
        Assert.Equal((ForeignRiotOrderOwnership.Unproven, "OWN_DEMAND_LEG_ORDER_ABANDONED"), (held.Ownership, held.OwnershipBasis));
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));

        fixture.Riot.ForgetOrder(oldUpperId);
        fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.Unsettled, (await OrderRowAsync(fixture, orderId)).State);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));

        using Roster roster = new();
        ForeignRunningOrderManualReleaseResult result = await ReleaseAsync(
            fixture, roster, Request(fixture) with { RiotOrderId = orderId });

        Assert.True(result.Released, string.Join(',', result.Codes));
        for (int round = 0; round < 3; round++)
        {
            fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }
        ForeignRiotOrderRow released = await OrderRowAsync(fixture, orderId);
        Assert.Equal(
            (ForeignRiotOrderStates.ReleasedByOperator, ForeignRiotOrderOwnership.Unproven, "OWN_DEMAND_LEG_ORDER_ABANDONED"),
            (released.State, released.Ownership, released.OwnershipBasis));
        Assert.Empty(await HeldAsync(fixture));
        Assert.Empty(fixture.Riot.OrderCommands);
    }

    private static async Task<ForeignRiotOrderRow> OrderRowAsync(RuntimeFixture fixture, string orderId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.ForeignRiotOrders.AsNoTracking().SingleAsync(row => row.RiotOrderId == orderId, Token);
    }

    /// <summary>
    /// 审查 S1：放开靠的是「这一次读不到」；放开之后这张单以挂起（8，REQ-0164 算占车）重新出现在列表里、执行车辆不是别人的车时，退回
    /// <c>UNSETTLED</c> 重新挡车。执行车辆是不归本服务端管的车时不碰，仍是放开。
    /// </summary>
    [Theory]
    [InlineData("on-our-vehicle", true)]
    [InlineData("without-a-vehicle", true)]
    [InlineData("on-another-vehicle", false)]
    [Trait("Requirement", "REQ-0164")]
    public async Task AReleasedOrderListedAgainSuspendedHoldsAgainUnlessAnotherVehicleRunsIt(string where, bool heldAgain)
    {
        await using RuntimeFixture fixture = await UnsettledAsync();
        using Roster roster = new();
        Assert.True((await ReleaseAsync(fixture, roster, Request(fixture))).Released);

        fixture.Riot.PlaceOrder(
            WebPageOrderId,
            "",
            RiotOrderState.Suspended,
            where switch
            {
                "on-our-vehicle" => fixture.Options.VehicleKey,
                "on-another-vehicle" => "BROKERX-0000000000000000000000000000ffff",
                _ => null,
            });
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);

        ForeignRiotOrderRow row = await RowAsync(fixture);
        if (heldAgain)
        {
            Assert.Equal((ForeignRiotOrderStates.Unsettled, (DateTimeOffset?)null), (row.State, row.EndedAt));
            Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        }
        else
        {
            Assert.Equal(ForeignRiotOrderStates.ReleasedByOperator, row.State);
            Assert.Empty(await HeldAsync(fixture));
        }
        Assert.Empty(fixture.Riot.OrderCommands);
    }

    /// <summary>
    /// 审查 L1：从「发过取消」走到 <c>UNSETTLED</c> 的行带着那一次取消的审计；人工放开像监看自己的终结一样把它收尾——回读读到 CANCELLED 为
    /// CONFIRMED，读到别的终结为 FAILED，读不到状态（查无此单）为 UNKNOWN（增量审查 L-1：与监看的 <c>Outcome</c> 一致）；行上的取消结果在读到终结时
    /// 一并改写。
    /// </summary>
    [Theory]
    [InlineData(RiotOrderState.Cancelled, "Confirmed", "CANCELLED")]
    [InlineData(null, "Unknown", "STILL_RUNNING")]
    [Trait("Requirement", "REQ-0164")]
    public async Task AReleaseSettlesTheCancelAttemptOfARowThatHadSentOne(int? readBack, string outcome, string cancelResult)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.CancelTakesEffect = false;
        fixture.Riot.PlaceOrder(WebPageOrderId, "", RiotOrderState.Executing, fixture.Options.VehicleKey);
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.StillRunningAfterCancel, (await RowAsync(fixture)).State);
        fixture.Riot.ForgetPlacedOrder(WebPageOrderId);
        fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        await fixture.Engine.ExecuteOnceAsync(Token);
        ForeignRiotOrderRow unsettled = await RowAsync(fixture);
        Assert.Equal(ForeignRiotOrderStates.Unsettled, unsettled.State);
        string auditId = Assert.IsType<string>(unsettled.CancelCommandAuditId);
        Assert.DoesNotContain(
            await CancelOutcomeAsync(fixture, auditId),
            new[] { RiotOrderCommandOutcome.Confirmed, RiotOrderCommandOutcome.Failed });
        if (readBack is { } ended)
        {
            fixture.Riot.EndPlacedOrder(WebPageOrderId, ended);
        }
        using Roster roster = new();

        ForeignRunningOrderManualReleaseResult result = await ReleaseAsync(fixture, roster, Request(fixture));

        Assert.True(result.Released, string.Join(',', result.Codes));
        Assert.Equal(outcome, (await CancelOutcomeAsync(fixture, auditId)).ToString());
        Assert.Equal(cancelResult, (await RowAsync(fixture)).CancelResult);
        using JsonDocument detail = JsonDocument.Parse(Assert.Single(await AuditsAsync(fixture)).DetailJson!);
        Assert.Equal(outcome, detail.RootElement.GetProperty("cancelSettledAs").GetString());
        Assert.Single(fixture.Riot.OrderCommands);
    }

    /// <summary>
    /// 审查 M1：入口挂在自己的开关 <c>ForeignRunningOrderRelease:enabled</c> 上——故障恢复关着、它开着时有路由；它关着时没有路由（请求 404），
    /// 哪怕故障恢复开着。
    /// </summary>
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("false", "true", true)]
    [InlineData("true", "false", false)]
    [InlineData("true", null, false)]
    [InlineData(null, "true", true)]
    public async Task TheEntryIsMappedOnItsOwnSwitchWhateverFaultRecoverySays(string? recovery, string? release, bool mapped)
    {
        Microsoft.AspNetCore.Builder.WebApplicationBuilder builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        if (recovery is not null)
        {
            builder.Configuration[VehicleFaultRecoveryEndpoints.EnabledKey] = recovery;
        }
        if (release is not null)
        {
            builder.Configuration[ForeignRunningOrderReleaseOptions.EnabledKey] = release;
        }
        // Registered so that the handlers' parameters read as services, never resolved: routing is all this looks at.
        builder.Services.AddScoped<ControlServer.Host.Runtime.Faults.VehicleFaultRecoveryService>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<WaitingPointArrivalSettlement>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<ControlServer.Host.Runtime.Recovery.RecoverySessionAdministratorClose>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<ForeignRunningOrderManualRelease>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<VehicleRoster>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<ControlServerDbContext>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddScoped<IGovernanceAuditWriter>(_ => throw new InvalidOperationException("not resolved"));
        builder.Services.AddSingleton(TimeProvider.System);
        await using Microsoft.AspNetCore.Builder.WebApplication app = builder.Build();

        app.MapVehicleFaultRecoveryEntriesWhenEnabled();
        bool said = app.MapForeignRunningOrderManualReleaseWhenEnabled();

        string[] routes =
        [
            .. ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText!)
        ];
        Assert.Equal(mapped, said);
        Assert.Equal(mapped, routes.Contains(ForeignRunningOrderManualReleaseEndpoints.Route));
        Assert.Equal(recovery == "true", routes.Contains(WaitingPointArrivalSettlementEndpoints.Route));
    }

    /// <summary>
    /// 增量审查 S-1：入口开着而凭据变量没有值时，启动时告警一次（事件 2235），服务端照样起来；入口关着、或凭据在，都不告警。答出的状态进
    /// <c>EFFECTIVE_CONFIGURATION</c>。
    /// </summary>
    [Theory]
    [InlineData(true, null, true, false)]
    [InlineData(true, "  ", true, false)]
    [InlineData(true, "release-credential", false, true)]
    [InlineData(false, null, false, false)]
    public void TheStartupCheckWarnsOnlyWhenTheEntryIsOnWithoutItsCredential(
        bool enabled, string? credential, bool warns, bool present)
    {
        RecordingLogger<ForeignRunningOrderManualReleaseTests> log = new();
        VehicleFaultRecoveryOptions recovery = new() { CredentialEnvironmentVariable = "CS617_TEST_CREDENTIAL" };

        ForeignRunningOrderReleaseState state = ForeignRunningOrderReleaseStartup.Check(
            log,
            new ForeignRunningOrderReleaseOptions { Enabled = enabled },
            recovery,
            name => name == "CS617_TEST_CREDENTIAL" ? credential : throw new InvalidOperationException($"asked for {name}"));

        Assert.Equal(new ForeignRunningOrderReleaseState(enabled, present), state);
        if (warns)
        {
            (LogLevel level, string message) = Assert.Single(log.Entries);
            Assert.Equal(LogLevel.Warning, level);
            Assert.Contains("CS617_TEST_CREDENTIAL", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(log.Entries);
        }
    }

    /// <summary>
    /// 增量审查 S-1：<c>EFFECTIVE_CONFIGURATION</c> 末尾带上这个入口，恰好两个布尔（开没开、凭据在不在），经宿主同一个格式化器写出；之前那几项的属性名
    /// 不变（七个占位符超出 <c>LoggerMessage.Define</c> 的上限，事件改为直接交带 <c>{OriginalFormat}</c> 的状态；不用日志源生成器，它把集合格式化成字符串）。
    /// </summary>
    [Fact]
    public void TheEffectiveConfigurationEventCarriesTheReleaseEntryAsExactlyTwoBooleans()
    {
        using StringWriter writer = new();
        using (Serilog.Core.Logger serilog = new Serilog.LoggerConfiguration().WriteTo.Sink(new CompactJsonSink(writer)).CreateLogger())
        using (Serilog.Extensions.Logging.SerilogLoggerFactory factory = new(serilog))
        {
            EffectiveConfigurationEvent.Log(
                factory.CreateLogger("ControlServer.Host"),
                new JourneyRuntimeOptions { AllowedWorkTypes = ["STAGING_TO_WIRE"], AllowedDispatchZones = ["WIRE"] },
                new ControlServer.Host.Runtime.CreateGate.MapStationCatalogOptions(),
                "http://127.0.0.1:58188",
                new ForeignRunningOrderReleaseState(Enabled: true, CredentialPresent: false));
        }

        using JsonDocument logged = JsonDocument.Parse(writer.ToString().Trim());
        JsonElement root = logged.RootElement;
        Assert.StartsWith("EFFECTIVE_CONFIGURATION ", root.GetProperty("@mt").GetString(), StringComparison.Ordinal);
        // No longer the last field since control-server#672 appended the admission policy and the two waits.
        Assert.Contains(" foreignRunningOrderRelease={ForeignRunningOrderRelease} ", root.GetProperty("@mt").GetString(), StringComparison.Ordinal);
        JsonElement release = root.GetProperty("ForeignRunningOrderRelease");
        Assert.Equal(
            [("CredentialPresent", JsonValueKind.False), ("Enabled", JsonValueKind.True)],
            release.EnumerateObject().Select(item => (item.Name, item.Value.ValueKind)).OrderBy(item => item.Name, StringComparer.Ordinal));
        Assert.Equal("STAGING_TO_WIRE", Assert.Single(root.GetProperty("AllowedWorkTypes").EnumerateArray().ToArray()).GetString());
        Assert.Equal("http://127.0.0.1:58188", root.GetProperty("MesIngestBaseUrl").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("ClearanceToWaitingPointEnabled").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("MapStationCatalog").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("Fleet").ValueKind);
        Assert.Equal(5350, root.GetProperty("EventId").GetProperty("Id").GetInt32());
    }

    /// <summary>宿主的格式化器（CompactJsonFormatter），写进一个字符串。</summary>
    private sealed class CompactJsonSink(TextWriter writer) : Serilog.Core.ILogEventSink
    {
        private readonly Serilog.Formatting.Compact.CompactJsonFormatter _formatter = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => _formatter.Format(logEvent, writer);
    }

    // ---- 人工出口：拒绝 -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 每一条不满足的前提都拒绝：不改记录、车照样被按住，写一条失败的管理员审计，审计与结果里都有那个码，每个码都有中文说明。
    /// </summary>
    [Theory]
    [MemberData(nameof(ReleasePremises))]
    [Trait("Requirement", "REQ-0164")]
    public async Task EachUnmetPremiseRefusesTheReleaseChangesNothingAndIsAudited(string premise, string code)
    {
        await using RuntimeFixture fixture = await UnsettledAsync();
        using Roster roster = new();
        ForeignRunningOrderManualReleaseRequest request = Request(fixture);
        IRiotOrderListingFacts? listing = null;
        switch (premise)
        {
            case "operator-missing": request = request with { OperatorId = " " }; break;
            case "not-authorized": request = request with { OperatorId = "somebody-else" }; break;
            case "reason-missing": request = request with { Reason = null }; break;
            case "site-verification-missing": request = request with { SiteVerification = "" }; break;
            case "field-too-long": request = request with { Reason = new string('x', ForeignRunningOrderManualRelease.MaxTextLength + 1) }; break;
            case "order-id-too-long": request = request with { RiotOrderId = new string('o', ForeignRunningOrderManualRelease.MaxIdentifierLength + 1) }; break;
            case "other-vehicle": request = request with { AgvId = "AGV-OTHER", VehicleKey = "KEY-OTHER" }; break;
            case "unknown-order": request = request with { RiotOrderId = "order-nobody" }; break;
            case "not-unsettled":
                await SetRowAsync(fixture, row => row.State = ForeignRiotOrderStates.HeldCancelNotAuthorized);
                break;
            case "listing-incomplete": fixture.Riot.ListingComplete = false; break;
            case "listing-unreachable": fixture.Riot.ListingThrows = new HttpRequestException("RIoT did not answer."); break;
            case "still-listed-running":
                fixture.Riot.PlaceOrder(WebPageOrderId, "", RiotOrderState.Executing, fixture.Options.VehicleKey);
                break;
            case "still-listed-suspended":
                fixture.Riot.PlaceOrder(WebPageOrderId, "", RiotOrderState.Suspended, fixture.Options.VehicleKey);
                break;
            case "still-listed-queueing":
                fixture.Riot.PlaceOrder(WebPageOrderId, "", RiotOrderState.Queueing, null);
                break;
            case "read-suspended": fixture.Riot.EndPlacedOrder(WebPageOrderId, RiotOrderState.Suspended); break;
            case "read-running": fixture.Riot.EndPlacedOrder(WebPageOrderId, RiotOrderState.Executing); break;
            // Review P1: every unfinished state, and a value RIoT has never been seen to send, is "not ended".
            case "read-queueing": fixture.Riot.EndPlacedOrder(WebPageOrderId, RiotOrderState.Queueing); break;
            case "read-paused": fixture.Riot.EndPlacedOrder(WebPageOrderId, RiotOrderState.Paused); break;
            case "read-hang": fixture.Riot.EndPlacedOrder(WebPageOrderId, RiotOrderState.Hang); break;
            case "read-queue-priority": fixture.Riot.EndPlacedOrder(WebPageOrderId, RiotOrderState.QueuePriority); break;
            case "read-unknown-state": fixture.Riot.EndPlacedOrder(WebPageOrderId, 11); break;
            // Review P2: listed running, its own read failing (the gateway's null) -- the listing alone refuses.
            case "still-listed-running-read-fails":
                fixture.Riot.PlaceOrder(WebPageOrderId, "", RiotOrderState.Executing, fixture.Options.VehicleKey);
                listing = new ReadAnswers(fixture.Riot, _ => null);
                break;
            // Review L3: a port that throws something other than the transport exceptions is "could not read", audited.
            case "listing-throws-other": fixture.Riot.ListingThrows = new InvalidOperationException("a defect in the port"); break;
            default: throw new ArgumentOutOfRangeException(nameof(premise), premise, null);
        }
        ForeignRiotOrderRow before = await RowAsync(fixture);

        ForeignRunningOrderManualReleaseResult result = await ReleaseAsync(fixture, roster, request, listing: listing);
        fixture.Riot.ListingComplete = true;
        fixture.Riot.ListingThrows = null;

        Assert.False(result.Released);
        Assert.Contains(code, result.Codes);
        Assert.True(ForeignRunningOrderManualRelease.Descriptions.ContainsKey(code));
        ForeignRiotOrderRow after = await RowAsync(fixture);
        Assert.Equal((before.State, before.UpdatedAt, before.EndedAt), (after.State, after.UpdatedAt, after.EndedAt));
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        AdministratorAuditRecordRow audit = Assert.Single(await AuditsAsync(fixture));
        Assert.Equal((result.AuditRecordId, GovernanceActionOutcome.Failed), (audit.AuditRecordId, audit.Outcome));
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson!);
        Assert.Equal("REJECTED", detail.RootElement.GetProperty("result").GetString());
        Assert.Contains(code, detail.RootElement.GetProperty("codes").EnumerateArray().Select(item => item.GetString()));
    }

    public static TheoryData<string, string> ReleasePremises => new()
    {
        { "operator-missing", ForeignRunningOrderManualRelease.OperatorRequired },
        { "not-authorized", ForeignRunningOrderManualRelease.NotAuthorized },
        { "reason-missing", ForeignRunningOrderManualRelease.ReasonRequired },
        { "site-verification-missing", ForeignRunningOrderManualRelease.SiteVerificationRequired },
        { "field-too-long", ForeignRunningOrderManualRelease.FieldTooLong },
        { "order-id-too-long", ForeignRunningOrderManualRelease.FieldTooLong },
        { "other-vehicle", ForeignRunningOrderManualRelease.OrderNotFound },
        { "unknown-order", ForeignRunningOrderManualRelease.OrderNotFound },
        { "not-unsettled", ForeignRunningOrderManualRelease.NotUnsettled },
        { "listing-incomplete", ForeignRunningOrderManualRelease.ListingUnreadable },
        { "listing-unreachable", ForeignRunningOrderManualRelease.ListingUnreadable },
        { "still-listed-running", ForeignRunningOrderManualRelease.OrderStillUnfinished },
        { "still-listed-suspended", ForeignRunningOrderManualRelease.OrderStillUnfinished },
        { "still-listed-queueing", ForeignRunningOrderManualRelease.OrderStillUnfinished },
        { "read-suspended", ForeignRunningOrderManualRelease.OrderReadNotEnded },
        { "read-running", ForeignRunningOrderManualRelease.OrderReadNotEnded },
        { "read-queueing", ForeignRunningOrderManualRelease.OrderReadNotEnded },
        { "read-paused", ForeignRunningOrderManualRelease.OrderReadNotEnded },
        { "read-hang", ForeignRunningOrderManualRelease.OrderReadNotEnded },
        { "read-queue-priority", ForeignRunningOrderManualRelease.OrderReadNotEnded },
        { "read-unknown-state", ForeignRunningOrderManualRelease.OrderReadNotEnded },
        { "still-listed-running-read-fails", ForeignRunningOrderManualRelease.OrderStillUnfinished },
        { "listing-throws-other", ForeignRunningOrderManualRelease.ListingUnreadable },
    };

    /// <summary>
    /// RIoT 在任何事务之外读；读的那一刻监看改过这一行（例如它刚又看见这张单）：事务里按读到的状态与更新时刻核对，核不上就拒绝，
    /// 什么也不写，答 <c>FOREIGN_ORDER_RELEASE_STATE_CHANGED</c>，并照样留一条失败审计。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task ARowChangedWhileRiotIsReadIsRefusedAndNothingIsWritten()
    {
        await using RuntimeFixture fixture = await UnsettledAsync();
        using Roster roster = new();
        DateTimeOffset changedAt = fixture.Clock.GetUtcNow().AddMilliseconds(1);
        fixture.Riot.BeforeListing = _ =>
        {
            using ControlServerDbContext other = new(fixture.DbOptionsForTests);
            ForeignRiotOrderRow row = other.ForeignRiotOrders.Single(item => item.RiotOrderId == WebPageOrderId);
            row.UpdatedAt = changedAt;
            other.SaveChanges();
        };

        ForeignRunningOrderManualReleaseResult result = await ReleaseAsync(fixture, roster, Request(fixture));
        fixture.Riot.BeforeListing = null;

        Assert.Equal([ForeignRunningOrderManualRelease.StateChanged], result.Codes);
        ForeignRiotOrderRow after = await RowAsync(fixture);
        Assert.Equal((ForeignRiotOrderStates.Unsettled, changedAt), (after.State, after.UpdatedAt));
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await AuditsAsync(fixture)).Outcome);
    }

    /// <summary>第二次办同一张单：第一次已放开，第二次答「不是 UNSETTLED」，什么也不改，审计两条（一成一败）。</summary>
    [Fact]
    public async Task ASecondReleaseOfTheSameOrderChangesNothing()
    {
        await using RuntimeFixture fixture = await UnsettledAsync();
        using Roster roster = new();
        Assert.True((await ReleaseAsync(fixture, roster, Request(fixture))).Released);
        DateTimeOffset releasedAt = (await RowAsync(fixture)).UpdatedAt;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));

        ForeignRunningOrderManualReleaseResult again = await ReleaseAsync(fixture, roster, Request(fixture));

        Assert.Equal([ForeignRunningOrderManualRelease.NotUnsettled], again.Codes);
        Assert.Equal(releasedAt, (await RowAsync(fixture)).UpdatedAt);
        Assert.Equal(
            [GovernanceActionOutcome.Succeeded, GovernanceActionOutcome.Failed],
            (await AuditsAsync(fixture)).Select(row => row.Outcome));
    }

    /// <summary>入口的每一个拒绝码都有给现场人员看的中文说明（按反射核，新加一个码而忘了说明就红）。</summary>
    [Fact]
    public void EveryReleaseRefusalCodeHasAChineseDescription()
    {
        string[] codes =
        [
            .. typeof(ForeignRunningOrderManualRelease)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Where(value => value.StartsWith("FOREIGN_ORDER_RELEASE_", StringComparison.Ordinal)),
        ];
        Assert.Equal(11, codes.Length);
        Assert.Equal(codes.Order(StringComparer.Ordinal), ForeignRunningOrderManualRelease.Descriptions.Keys.Order(StringComparer.Ordinal));
        Assert.All(ForeignRunningOrderManualRelease.Descriptions.Values, text => Assert.False(string.IsNullOrWhiteSpace(text)));
    }

    /// <summary>
    /// 看板上每一句 <c>FOREIGN_RUNNING_ORDER_UNSETTLED</c> 的说明——通用的、本服务端自己放弃的三种单的——都指向这个入口，没有一句还说
    /// 「没有人工解除入口」。
    /// </summary>
    [Fact]
    public void EveryUnsettledDashboardDescriptionPointsAtTheManualRelease()
    {
        IReadOnlyDictionary<string, string>[] sets =
        [
            ControlServer.Host.Dashboard.ForeignRunningOrdersQueryEndpoint.Descriptions,
            .. ControlServer.Host.Dashboard.ForeignRunningOrdersQueryEndpoint.OwnAbandonedOrderDescriptions.Values,
        ];
        // control-server#673 added the fifth: a demand leg's order taken as never created.
        Assert.Equal(5, sets.Length);
        Assert.All(sets, set =>
        {
            string text = set[ForeignRunningOrders.UnsettledReason];
            Assert.EndsWith(ControlServer.Host.Dashboard.ForeignRunningOrdersQueryEndpoint.ManualReleaseHint, text, StringComparison.Ordinal);
            Assert.DoesNotContain("没有人工解除入口", text, StringComparison.Ordinal);
        });
    }

    // ---- Host 入口 ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Host 入口的边界（判定是上面几条的事）：故障恢复那把凭据，不对答 401、什么都不写；没点名车或订单答 422、不写审计；不是本服务端的车答 404；
    /// 拒绝答 409，列出全部码与每个码的中文说明，并写失败审计；办成答 200，带审计号。凭据变量为空答 503。
    /// </summary>
    [Fact]
    public async Task TheHostEntryAuthenticatesNamesTheVehicleAndMapsTheDecisionOntoHttp()
    {
        await using RuntimeFixture fixture = await UnsettledAsync();
        using Roster roster = new();
        string variable = "CONTROL_SERVER_TEST_" + Guid.NewGuid().ToString("N");
        ForeignRunningOrderManualReleaseHttpRequest request = new(
            fixture.Options.AgvId, WebPageOrderId, Operator, "网页手动单已在 RIoT 删除", "SITE-617-02");
        Assert.Equal(503, ProblemOf(await PostAsync(fixture, roster, variable, "Bearer release-credential", request)).StatusCode);
        Environment.SetEnvironmentVariable(variable, "release-credential");
        try
        {
            Assert.IsType<UnauthorizedHttpResult>((await PostAsync(fixture, roster, variable, "Bearer wrong", request)).Result);
            Assert.Equal(422, ProblemOf(await PostAsync(fixture, roster, variable, "Bearer release-credential", request with { RiotOrderId = " " })).StatusCode);
            Assert.Equal(422, ProblemOf(await PostAsync(fixture, roster, variable, "Bearer release-credential", request with { AgvId = null })).StatusCode);
            Assert.Equal(404, ProblemOf(await PostAsync(fixture, roster, variable, "Bearer release-credential", request with { AgvId = "agv-nobody" })).StatusCode);
            Assert.Empty(await AuditsAsync(fixture));

            ProblemHttpResult refused = ProblemOf(await PostAsync(
                fixture, roster, variable, "Bearer release-credential", request with { OperatorId = "somebody-else", SiteVerification = null }));
            Assert.Equal(409, refused.StatusCode);
            Assert.Equal(
                [ForeignRunningOrderManualRelease.SiteVerificationRequired, ForeignRunningOrderManualRelease.NotAuthorized],
                Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.ProblemDetails.Extensions["codes"]));
            IReadOnlyDictionary<string, string> descriptions =
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(refused.ProblemDetails.Extensions["descriptions"]);
            Assert.Equal(
                ForeignRunningOrderManualRelease.Descriptions[ForeignRunningOrderManualRelease.NotAuthorized],
                descriptions[ForeignRunningOrderManualRelease.NotAuthorized]);
            Assert.Equal(GovernanceActionOutcome.Failed, Assert.Single(await AuditsAsync(fixture)).Outcome);
            Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));

            var released = await PostAsync(fixture, roster, variable, "Bearer release-credential", request);
            ForeignRunningOrderManualReleaseResponse body = Assert.IsType<Ok<ForeignRunningOrderManualReleaseResponse>>(released.Result).Value!;
            Assert.Equal(("RELEASED", WebPageOrderId), (body.Outcome, body.RiotOrderId));
            Assert.Equal(body.AuditRecordId, (await AuditsAsync(fixture))[^1].AuditRecordId);
            Assert.Empty(await HeldAsync(fixture));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 走引擎走到 <c>UNSETTLED</c>：网页手动单（upperId 空串）跑在我们车上，取消开关关着（只按住、不取消），然后它离开运行列表、
    /// 按单号回读什么也答不出，过了落定时间。前置条件断言在这里。
    /// </summary>
    private static async Task<RuntimeFixture> UnsettledAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.ForeignOrderCancel.Enabled = false;
        fixture.Riot.PlaceOrder(WebPageOrderId, "", RiotOrderState.Executing, fixture.Options.VehicleKey);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.HeldCancelNotAuthorized, (await RowAsync(fixture)).State);

        fixture.Riot.ForgetPlacedOrder(WebPageOrderId);
        fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.Unsettled, (await RowAsync(fixture)).State);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        Assert.Empty(fixture.Riot.OrderCommands);
        return fixture;
    }

    private static ForeignRunningOrderManualReleaseRequest Request(RuntimeFixture fixture) =>
        new(fixture.Options.AgvId, fixture.Options.VehicleKey, WebPageOrderId, Operator, "网页手动单在 RIoT 里查不到，已与现场核对", "SITE-617-01");

    private static ForeignRunningOrderManualRelease Release(
        RuntimeFixture fixture,
        Roster roster,
        ILogger<ForeignRunningOrderManualRelease>? log = null,
        IRiotOrderListingFacts? listing = null) =>
        new(
            fixture.Context,
            listing ?? fixture.Riot,
            new FieldOperatorRoleRoster(Options.Create(new FieldOperatorRoleOptions { Path = roster.Path })),
            new GovernanceStore(fixture.Context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default),
            fixture.Clock,
            log ?? NullLogger<ForeignRunningOrderManualRelease>.Instance);

    private static async Task<ForeignRunningOrderManualReleaseResult> ReleaseAsync(
        RuntimeFixture fixture,
        Roster roster,
        ForeignRunningOrderManualReleaseRequest request,
        ILogger<ForeignRunningOrderManualRelease>? log = null,
        IRiotOrderListingFacts? listing = null)
    {
        fixture.Context.ChangeTracker.Clear();
        ForeignRunningOrderManualReleaseResult result = await Release(fixture, roster, log, listing).DecideAsync(request, Token);
        fixture.Context.ChangeTracker.Clear();
        return result;
    }

    private static async Task<Results<Ok<ForeignRunningOrderManualReleaseResponse>, UnauthorizedHttpResult, ProblemHttpResult>> PostAsync(
        RuntimeFixture fixture, Roster roster, string credentialVariable, string authorization, ForeignRunningOrderManualReleaseHttpRequest request)
    {
        DefaultHttpContext http = new();
        http.Request.Headers.Authorization = authorization;
        fixture.Context.ChangeTracker.Clear();
        var result = await ForeignRunningOrderManualReleaseEndpoints.HandleAsync(
            http,
            request,
            Release(fixture, roster),
            new VehicleRoster(Options.Create(fixture.Options)),
            Options.Create(new VehicleFaultRecoveryOptions { Enabled = true, CredentialEnvironmentVariable = credentialVariable }),
            Token);
        fixture.Context.ChangeTracker.Clear();
        return result;
    }

    private static ProblemHttpResult ProblemOf(
        Results<Ok<ForeignRunningOrderManualReleaseResponse>, UnauthorizedHttpResult, ProblemHttpResult> result) =>
        Assert.IsType<ProblemHttpResult>(result.Result);

    private static ForeignRunningOrderSupervisor Supervisor(RuntimeFixture fixture, IRiotOrderListingFacts listing) =>
        new(
            fixture.Context,
            listing,
            fixture.Riot,
            new RiotOrderCommandAuditStore(fixture.Context),
            new VehicleRoster(Options.Create(fixture.Options)),
            Options.Create(fixture.Options),
            Options.Create(new RiotForeignOrderCancelOptions { Enabled = false }),
            fixture.Clock,
            NullLogger<ForeignRunningOrderSupervisor>.Instance);

    private static async Task<string[]> HeldAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return [.. (await ForeignRunningOrders.HeldAgvIdsAsync(reading, Token)).Order(StringComparer.Ordinal)];
    }

    private static async Task<ForeignRiotOrderRow> RowAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.ForeignRiotOrders.AsNoTracking().SingleAsync(row => row.RiotOrderId == WebPageOrderId, Token);
    }

    private static async Task SetRowAsync(RuntimeFixture fixture, Action<ForeignRiotOrderRow> change)
    {
        await using ControlServerDbContext other = new(fixture.DbOptionsForTests);
        ForeignRiotOrderRow row = await other.ForeignRiotOrders.SingleAsync(item => item.RiotOrderId == WebPageOrderId, Token);
        change(row);
        await other.SaveChangesAsync(Token);
    }

    private static async Task<RiotOrderCommandOutcome> CancelOutcomeAsync(RuntimeFixture fixture, string auditId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return (await reading.RiotOrderCommandAudit.AsNoTracking().SingleAsync(row => row.CommandAuditId == auditId, Token)).Outcome;
    }

    private static async Task<AdministratorAuditRecordRow[]> AuditsAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        AdministratorAuditRecordRow[] rows = await reading.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action == ForeignRunningOrderManualRelease.AuditAction)
            .ToArrayAsync(Token);
        return [.. rows.OrderBy(row => row.RecordedAtUtcTicks)];
    }

    /// <summary><see cref="RecordingRiot"/> 的列表照旧，按单号回读由 <paramref name="read"/> 回答（审查 P2、L3）。</summary>
    private sealed class ReadAnswers(RecordingRiot inner, Func<string, int?> read) : IRiotOrderListingFacts
    {
        public Task<RiotUnfinishedOrderListing> ListUnfinishedOrdersAsync(CancellationToken cancellationToken) =>
            inner.ListUnfinishedOrdersAsync(cancellationToken);

        public Task<RiotOrderStateReading> ReadOrderStateAsync(string orderId, CancellationToken cancellationToken) =>
            Task.FromResult(new RiotOrderStateReading(orderId, read(orderId), DateTimeOffset.UnixEpoch));
    }

    /// <summary>R-11／R-13 名单文件，只有 <see cref="Operator"/> 一人（R-11）。</summary>
    private sealed class Roster : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cs617-roster-{Guid.NewGuid():N}.json");

        public Roster() => File.WriteAllText(Path, $$"""{"operators":[{"operatorId":"{{Operator}}","roles":["R-11"]}]}""");

        public void Dispose() => File.Delete(Path);
    }

    /// <summary>
    /// HTTP 那一层的 RIoT，只有那张网页手动单：<see cref="Running"/> 时按状态列单把它列成在我们车上执行；按单号回读按现场形态回答
    /// （upperId 空串），<see cref="DetailState"/> 为空时回 503。
    /// </summary>
    private sealed class WebPageRiot(string vehicleKey) : HttpMessageHandler
    {
        public bool Running { get; set; }

        public int? DetailState { get; set; }

        public List<string> Paths { get; } = [];

        public RiotSession Session() => new(new RiotOptions { BaseUrl = "http://riot.test", CallApiKey = "test-call-api-key" }, this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            lock (Paths)
            {
                Paths.Add(path);
            }
            if (path == "/api/order/v1/orderRecord")
            {
                string records = Running
                    ? $$"""[{"id":1757698,"orderId":"{{WebPageOrderId}}","upperId":"","orderState":3,"appointVehicleKey":"","executeVehicleKey":"{{vehicleKey}}"}]"""
                    : "[]";
                return Json($$$"""{"code":"0","message":"成功","result":{"current":1,"size":100,"total":{{{(Running ? 1 : 0)}}},"records":{{{records}}}}}""");
            }
            if (path == "/api/order/v1/orderRecord/detailByOrderId/" + WebPageOrderId)
            {
                return DetailState is { } state
                    ? Json($$"""{"code":"0","message":"成功","msgDetail":"","result":{"id":1757698,"orderId":"{{WebPageOrderId}}","upperId":"","orderName":"iiii","createdBy":"admin","orderState":{{state}},"appointVehicleKey":"","executeVehicleKey":"{{vehicleKey}}"},"tid":""}""")
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                    });
            }
            throw new InvalidOperationException($"Unexpected RIoT call {path}.");
        }

        private static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
