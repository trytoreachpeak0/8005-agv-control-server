using System.Net;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// control-server#611：建单回 HTTP 200 加业务失败码（现场 2026-10-11 两次 <c>00002</c>）时网关读成什么，以及 RIoT 的原话有没有留下来。
/// </summary>
public sealed class HttpRiotFailureDetailTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 11, 4, 50, 7, TimeSpan.Zero);

    /// <summary>
    /// 网关把 00002 读成结果未知，回执带 HTTP 200、业务码、<c>RIOT_API_FAILURE</c>——与超时（<c>TIMEOUT</c>、没有 HTTP 码）分得开。
    /// 引擎测试里的替身（<c>IdleReturnExecutionTests.BusinessFailure00002</c>）就照这个回执写。RIoT 的 <c>message</c> 进告警日志（事件 2166）；
    /// 只发一次请求，不重试。
    /// </summary>
    [Fact]
    public async Task ABusinessFailureOnCreateReadsAsUnknownWithItsCodesAndRiotsWordsAreLogged()
    {
        EventRecordingLogger<HttpRiotMovementGateway> log = new();
        (RiotOrderObservation result, int calls) = await CreateAsync(
            """{"code":"00002","message":"内部错误","msgDetail":"java.lang.NullPointerException","result":null}""", log);

        Assert.Equal(1, calls);
        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal(
            new RiotOrderCallReceipt("CREATE", "SdkFailure", ReadAt, 200, "00002", null, "RIOT_API_FAILURE"),
            result.Receipt);
        Assert.False(result.IsExactAbsentAtObservation(Intent.UpperId));

        var entry = Assert.Single(log.Entries);
        Assert.Equal((2166, LogLevel.Warning), (entry.EventId.Id, entry.Level));
        Assert.Contains(Intent.UpperId, entry.Message, StringComparison.Ordinal);
        Assert.Contains("00002", entry.Message, StringComparison.Ordinal);
        Assert.Contains("内部错误", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>超时：同样结果未知，但回执是 <c>TIMEOUT</c>、没有业务码——两种在审计里分得开。</summary>
    [Fact]
    public async Task ATimedOutCreateIsAlsoUnknownButCarriesNoBusinessCode()
    {
        EventRecordingLogger<HttpRiotMovementGateway> log = new();
        (RiotOrderObservation result, _) = await CreateAsync(null, log);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Equal(("SdkFailure", null, "TIMEOUT"), (result.Receipt!.Classification, result.Receipt.BusinessCode, result.Receipt.FailureCategory));
        Assert.Equal(2166, Assert.Single(log.Entries).EventId.Id);
    }

    /// <summary>日志里的原话压成一行、像凭据的值遮掉、截到 1000 字。</summary>
    [Fact]
    public async Task TheLoggedWordsAreOneLineMaskedAndCut()
    {
        EventRecordingLogger<HttpRiotMovementGateway> log = new();
        string message = "第一行\\n第二行 token=abc123secret Authorization: Bearer xyz789 " + new string('长', 1500);
        await CreateAsync($$"""{"code":"00002","message":"{{message}}","result":null}""", log);

        string logged = Assert.Single(log.Entries).Message;
        Assert.DoesNotContain("\n", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123secret", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz789", logged, StringComparison.Ordinal);
        Assert.Contains("…(truncated)", logged, StringComparison.Ordinal);
        Assert.True(logged.Length < 1300, logged.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 审查 S5：对账（RECONCILE）只有 RIoT 回了业务码才按 Information 记原话（事件 2167）；超时、断线这种没有业务回答的只记 Debug（事件 2168），
    /// 不然 RIoT 断线时每辆在途车每轮两条，一小时上万行。
    /// </summary>
    [Fact]
    public async Task AReconcileFailureIsLoggedAtInformationOnlyWhenRiotAnsweredWithACode()
    {
        EventRecordingLogger<HttpRiotMovementGateway> answered = new();
        await ReconcileAsync("""{"code":"00002","message":"内部错误","result":null}""", answered);
        var entry = Assert.Single(answered.Entries);
        Assert.Equal((2167, LogLevel.Information), (entry.EventId.Id, entry.Level));
        Assert.Contains("内部错误", entry.Message, StringComparison.Ordinal);

        EventRecordingLogger<HttpRiotMovementGateway> unanswered = new();
        await ReconcileAsync(null, unanswered);
        Assert.Equal((2168, LogLevel.Debug), (Assert.Single(unanswered.Entries).EventId.Id, Assert.Single(unanswered.Entries).Level));
    }

    private static async Task ReconcileAsync(string? body, EventRecordingLogger<HttpRiotMovementGateway> log)
    {
        CallbackHandler handler = new(() => body is null
            ? throw new TaskCanceledException("simulated timeout")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        await using RiotSession session = new(
            new RiotOptions { BaseUrl = "http://riot.test", CallApiKey = "test-call-api-key" }, handler);
        HttpRiotMovementGateway gateway = new(session, new FixedTimeProvider(ReadAt), log);
        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(Intent.UpperId, TestContext.Current.CancellationToken);
        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
    }

    private static readonly OrderIntent Intent = new(
        "LEG-IDLE", "IDLE-RETURN", "W2G-IDLE-BROKERX-0001", "TO_WAITING_POINT", "ST-216",
        ReadAt, "AGV-8005-02", 26, 216, 1, 1, OrderShapes.SingleMove);

    /// <summary>建一次单，RIoT 回 <paramref name="body"/>（HTTP 200）；为空则模拟超时。</summary>
    private static async Task<(RiotOrderObservation, int)> CreateAsync(
        string? body, EventRecordingLogger<HttpRiotMovementGateway> log)
    {
        int calls = 0;
        CallbackHandler handler = new(() =>
        {
            calls++;
            return body is null
                ? throw new TaskCanceledException("simulated timeout")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        await using RiotSession session = new(
            new RiotOptions { BaseUrl = "http://riot.test", CallApiKey = "test-call-api-key" }, handler);
        HttpRiotMovementGateway gateway = new(session, new FixedTimeProvider(ReadAt), log);
        RiotOrderObservation result = await gateway.CreateAsync(Intent, TestContext.Current.CancellationToken);
        return (result, calls);
    }

    private sealed class CallbackHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
