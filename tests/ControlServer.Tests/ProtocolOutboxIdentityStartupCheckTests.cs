using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>
/// 启动守卫（control-server#382 审查必修 M1）：发件箱里有未确认、未退役、信封身份不是本构建的行时拒绝启动。
/// </summary>
/// <remarks>
/// <para>
/// 为什么：补发（<c>OnboardJourneyPublisher.ReplayPendingForSessionAsync</c>）只改 <c>sessionGeneration</c> 与 <c>sentAt</c>，
/// 信封身份照旧；在途旅程每轮以确定性 messageId 重发时 <c>WireToGateStore.RefreshOutboundEnvelopeAsync</c> 逐字段比身份，
/// 抛 <c>ProtocolContentConflictException</c>；车载端按身份校验，报 <c>PROTOCOL_RELEASE_IDENTITY_MISMATCH</c> 断会话，每次重连补发
/// 又断——只能改库或换空库解开。升级（v2 → v3 候选）与回滚方向对称，所以守卫只问「是不是本构建的身份」，不问是哪一版。
/// </para>
/// <para>
/// 已确认或已退役的旧身份行不会再被补发，留着无害，照常启动。
/// </para>
/// </remarks>
public sealed class ProtocolOutboxIdentityStartupCheckTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string V2Manifest = "4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AnUnacknowledgedLineUnderAnotherProtocolIdentityRefusesTheStartAndSaysWhatToDo()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        await using ControlServerDbContext context = await CreateAsync(connection);
        context.ProtocolOutbox.Add(Row("a1000000-0000-4000-8000-000000000001", "AGV-001", current: true, acknowledged: false));
        context.ProtocolOutbox.Add(Row("a1000000-0000-4000-8000-000000000002", "AGV-002", current: false, acknowledged: false));
        context.ProtocolOutbox.Add(Row("a1000000-0000-4000-8000-000000000003", "AGV-002", current: false, acknowledged: false));
        await context.SaveChangesAsync(Token);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProtocolOutboxIdentityStartupCheck.EnsureAsync(context, NullLogger.Instance, "C:/data/controlserver.db", Token));

        Assert.StartsWith(ProtocolOutboxIdentityStartupCheck.ReasonCode, refused.Message, StringComparison.Ordinal);
        Assert.Contains("AGV-002: 2 line(s) under AGV_FULL_PRODUCT protocolVersion 3 release 2.0.0", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AGV-001", refused.Message, StringComparison.Ordinal);
        Assert.Contains(
            FormattableString.Invariant(
                $"This build speaks {ProtocolCandidateIdentity.ProfileId} protocolVersion {ProtocolCandidateIdentity.ProtocolVersion} release {ProtocolCandidateIdentity.ReleaseVersion}"),
            refused.Message, StringComparison.Ordinal);
        Assert.Contains("C:/data/controlserver.db", refused.Message, StringComparison.Ordinal);
        Assert.Contains("start the build that wrote them", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task OldIdentityLinesThatAreAcknowledgedOrFencedDoNotStopTheStart()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        await using ControlServerDbContext context = await CreateAsync(connection);
        context.ProtocolOutbox.Add(Row("a2000000-0000-4000-8000-000000000001", "AGV-001", current: false, acknowledged: true));
        ProtocolOutboxRow fenced = Row("a2000000-0000-4000-8000-000000000002", "AGV-001", current: false, acknowledged: false);
        fenced.FencedAt = DateTimeOffset.UnixEpoch;
        context.ProtocolOutbox.Add(fenced);
        context.ProtocolOutbox.Add(Row("a2000000-0000-4000-8000-000000000003", "AGV-001", current: true, acknowledged: false));
        await context.SaveChangesAsync(Token);

        await ProtocolOutboxIdentityStartupCheck.EnsureAsync(context, NullLogger.Instance, null, Token);
    }

    private static ProtocolOutboxRow Row(string messageId, string agvId, bool current, bool acknowledged) => new()
    {
        MessageId = messageId,
        MessageType = "CurrentStopWorklistSnapshot",
        PayloadJson = JsonSerializer.Serialize(new
        {
            protocolVersion = current ? ProtocolCandidateIdentity.ProtocolVersion : 3,
            profileId = "AGV_FULL_PRODUCT",
            protocolReleaseVersion = current ? ProtocolCandidateIdentity.ReleaseVersion : "2.0.0",
            protocolReleaseManifestSha256 = current ? ProtocolCandidateIdentity.ManifestSha256 : V2Manifest,
            messageType = "CurrentStopWorklistSnapshot",
            messageId,
            correlationId = (string?)null,
            agvId,
            sessionGeneration = 1,
            sentAt = "2026-09-30T09:00:00Z",
            payload = new { }
        }),
        CreatedAt = DateTimeOffset.UnixEpoch,
        AcknowledgedAt = acknowledged ? DateTimeOffset.UnixEpoch : null
    };

    private static async Task<ControlServerDbContext> CreateAsync(SqliteConnection connection)
    {
        ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync(Token);
        return context;
    }
}
