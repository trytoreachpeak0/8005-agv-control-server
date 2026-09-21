using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 按业务键抑制（批次7-05，control-server#210）的断言，给各条终结路径的端到端测试共用：终结之后键上有没有抑制、是哪一条。
/// </summary>
internal static class SuppressionAssertions
{
    internal static async Task<TransportDemandSuppression[]> AllAsync(ControlServerDbContext context) =>
        [.. (await context.Set<TransportDemandSuppressionRow>().AsNoTracking()
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(row => new TransportDemandSuppression(row.TransportDemandKey, row.DemandId, row.ReasonCode, row.SuppressedAt))];

    /// <summary>这条需求的业务键上恰有一条抑制，是这条需求、这个码写下的；库里也只有这一条。</summary>
    internal static async Task AssertSuppressedAsync(ControlServerDbContext context, string demandId, string reasonCode)
    {
        string key = await KeyOfAsync(context, demandId);
        TransportDemandSuppression suppression = Assert.Single(await AllAsync(context));
        Assert.Equal(key, suppression.TransportDemandKey);
        Assert.Equal(demandId, suppression.DemandId);
        Assert.Equal(reasonCode, suppression.ReasonCode);
    }

    /// <summary>库里只有一条受理行时的 <see cref="AssertSuppressedAsync(ControlServerDbContext, string, string)"/>。</summary>
    internal static async Task AssertTheDemandSuppressedAsync(ControlServerDbContext context, string reasonCode) =>
        await AssertSuppressedAsync(
            context,
            (await context.AcceptedDemands.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).DemandId,
            reasonCode);

    /// <summary>库里一条抑制都没有：这种结束不是本地取消（REQ-0156）。</summary>
    internal static async Task AssertNothingSuppressedAsync(ControlServerDbContext context) =>
        Assert.Empty(await AllAsync(context));

    private static async Task<string> KeyOfAsync(ControlServerDbContext context, string demandId) =>
        (await context.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken)).TransportDemandKey;
}
