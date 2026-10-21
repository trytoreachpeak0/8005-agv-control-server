using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

public sealed class JourneyRuntimeWorker(
    IServiceScopeFactory scopeFactory,
    JourneyMutationGate mutationGate,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<JourneyRuntimeWorker> logger,
    Faults.InTransitDoorReadings? doorReadings = null) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogDisabled = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2001, nameof(LogDisabled)),
        "Journey runtime is disabled; MesIngest polling and movement dispatch are fail-closed.");
    private static readonly Action<ILogger, Exception?> LogIterationFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2002, nameof(LogIterationFailed)),
        "Journey runtime iteration failed closed; no stage is inferred from memory.");
    private static readonly Action<ILogger, Exception?> LogReleaseFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2153, nameof(LogReleaseFailed)),
        "Demand release iteration failed; nothing was released this round and the next round judges again.");
    private static readonly Action<ILogger, Exception?> LogRouteGraphRefreshFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2156, nameof(LogRouteGraphRefreshFailed)),
        "Route graph refresh failed outside its own handling; the engine round runs, and the snapshot's age decides whether dispatch may use it.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        JourneyRuntimeOptions runtime = options.Value;
        if (!runtime.Enabled)
        {
            LogDisabled(logger, null);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        using PeriodicTimer timer = new(runtime.PollInterval, timeProvider);
        Task<bool>? tick = null;
        do
        {
            // The whole round -- engine, then release service -- holds the gate a person's fault recovery takes
            // (control-server#299): that request arrives on an HTTP thread and cannot join this loop the way the release
            // service does, and interleaved with a round it would have the engine record a new fault against a journey the
            // recovery had just closed. See JourneyMutationGate.
            IDisposable round;
            try
            {
                round = await mutationGate.EnterAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            using (round)
            {
                // The route graph refreshes before dispatch decides, so a round is decided against the freshest
                // snapshot the engine could get. A refresh that fails does not stop the round: it marks the snapshot
                // stale, and the admission chain blocks on that -- the same outcome, reached through the fail-closed
                // path rather than an exception.
                //
                // Its own scope and its own try (control-server#656). Sharing the engine's try, a refresh that threw --
                // a RIoT map read timing out, which the refresher did not catch then -- skipped the whole engine round:
                // every vehicle's advance, and with it the fault model's escalation, emergency-stop confirmation and
                // REQ-0248 re-trigger, the door supervision, the dispatch round. A refresh failure is a reason not to
                // dispatch, never a reason not to supervise. The engine running after one opens nothing: the
                // refresher's own failures mark the snapshot stale, and one that escaped before it could still leaves
                // a snapshot RouteGraphAccess judges against the clock, so it ages out past RuntimeStateMaxAge whether
                // or not anyone marked it.
                try
                {
                    await using AsyncServiceScope refreshScope = scopeFactory.CreateAsyncScope();
                    RouteGraph.RouteGraphOptions routeGraphOptions = refreshScope.ServiceProvider
                        .GetRequiredService<IOptions<RouteGraph.RouteGraphOptions>>().Value;
                    if (routeGraphOptions.Enabled)
                    {
                        RouteGraph.RouteGraphRefresher refresher = refreshScope.ServiceProvider
                            .GetRequiredService<RouteGraph.RouteGraphRefresher>();
                        await refresher.RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    LogRouteGraphRefreshFailed(logger, error);
                }

                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    JourneyRuntimeEngine engine = scope.ServiceProvider.GetRequiredService<JourneyRuntimeEngine>();
                    await engine.ExecuteOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    LogIterationFailed(logger, error);
                }

                // REQ-0328 的释放服务（批次7-10，control-server#215）跑在引擎推进<b>之后、同一个循环里</b>，不另开循环。
                // 两者都会改同一趟旅程：引擎读旅程在事务外、存在事务里，释放服务关旅程时若恰好插在中间，引擎那次保存会把
                // 阶段写回去，把刚关掉的旅程又打开。串行跑，两者按构造就不会交错——不靠锁，也不加乐观并发列（本票零迁移）。
                // 自己一个作用域、自己一个 try：引擎这一轮抛了（例如整轮中止），释放照样判；释放抛了也不影响下一轮引擎。
                // 它不依赖派车轮次，所以 Blocked 的车（不进轮次）照样被判到。
                try
                {
                    await using AsyncServiceScope releaseScope = scopeFactory.CreateAsyncScope();
                    await releaseScope.ServiceProvider.GetRequiredService<Release.DemandReleaseService>()
                        .RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    LogReleaseFailed(logger, error);
                }
            }
        }
        while (await NextRoundAsync().ConfigureAwait(false));

        // The next round runs on the timer's tick, or earlier when a driving vehicle's door grace runs out before it
        // (control-server#789, CP-0017 revision one, note 5: judged when the 3 s are up, not up to a poll interval later). An
        // early round leaves the tick pending, so the timer's rhythm is kept. A due time already past waits for the tick: the
        // round that was due has run, and a vehicle it did not reach (arrived, order ended) must not spin the loop.
        async Task<bool> NextRoundAsync()
        {
            tick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
            if (EarlyRoundAfter(doorReadings?.NextDueAt, timeProvider.GetUtcNow(), runtime.PollInterval) is TimeSpan wait)
            {
                Task delay = Task.Delay(wait, timeProvider, stoppingToken);
                try
                {
                    if (await Task.WhenAny(tick, delay).ConfigureAwait(false) == delay && !tick.IsCompleted)
                    {
                        await delay.ConfigureAwait(false);
                        return true;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return false;
                }
            }

            try
            {
                return await tick.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            finally
            {
                tick = null;
            }
        }
    }

    /// <summary>
    /// How long the worker waits before its next round when a door grace runs out at <paramref name="graceDue"/>: the time to it
    /// when that is sooner than the next tick, null to wait for the tick (control-server#789).
    /// </summary>
    internal static TimeSpan? EarlyRoundAfter(DateTimeOffset? graceDue, DateTimeOffset now, TimeSpan pollInterval) =>
        graceDue - now is TimeSpan wait && wait > TimeSpan.Zero && wait < pollInterval ? wait : null;
}
