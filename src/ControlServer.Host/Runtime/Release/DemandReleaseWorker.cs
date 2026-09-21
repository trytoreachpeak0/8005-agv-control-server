using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Release;

/// <summary>
/// 按旅程运行时的节拍跑 <see cref="DemandReleaseService"/>（批次7-10，control-server#215）。
/// </summary>
/// <remarks>
/// <para>
/// <b>自己一个循环，不挂在派车轮次里</b>：旅程 Blocked 的车不进轮次（<c>JourneyRuntimeEngine</c> 算 <c>underWay</c> 时把它排除），
/// 而「车坏了」恰恰常见于这种车。挂在引擎里又是本票不碰的文件。两个循环各开各的作用域与连接，写都走 <c>BEGIN IMMEDIATE</c>
/// 事务，由库串行化——释放的判定因此在写事务里再做一遍，不信本轮开头的读数。
/// </para>
/// <para>
/// 与引擎同一个开关：运行时关着（生产 CD 的默认）就什么都不做，不建单的服务端也不该去取消谁的订单。
/// </para>
/// </remarks>
public sealed class DemandReleaseWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<DemandReleaseWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogIterationFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2153, nameof(LogIterationFailed)),
        "Demand release iteration failed; nothing was released this round and the next round judges again.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JourneyRuntimeOptions runtime = options.Value;
        if (!runtime.Enabled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        using PeriodicTimer timer = new(runtime.PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DemandReleaseService>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                LogIterationFailed(logger, error);
            }
        }
    }
}
