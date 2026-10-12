# cs#656 证据摘要：路网刷新超时不再跳过整轮引擎

## 问题

`RouteGraphRefresher.RefreshOnceAsync` 的 catch 写的是 `when (error is not OperationCanceledException)`，不看令牌。
`HttpClient.Timeout` 到点抛的 `TaskCanceledException` 也是 `OperationCanceledException`，而 `HttpRouteGraphSource`
不像站点目录读取（`HttpRiotMovementGateway.ReadMapStationsAsync`）那样先放行停机、再把 SDK 失败转成 `InvalidDataException`。
于是超时漏出刷新器，路网不标 stale。worker 里刷新与 `engine.ExecuteOnceAsync` 同在一个 try，超时落进通用 catch（2002），
这一轮全车队的引擎都跳过：外来订单监看、公共站释放与预留、每台车的推进（故障模型的升级与急停、急停确认、REQ-0248 重触发、
仓门在途监看、订单结束时的门故障、离站等待超时、仓门未关超时）、派车与充电、轮末等待告警。

## 改法

1. 刷新器的 catch 改成引擎里通用的写法 `when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)`：
   调用方没要求取消的超时按刷新失败处理（记 EventId 1、标 `ROUTE_GRAPH_REFRESH_FAILED`），停机取消原样抛出。
2. worker 把刷新拆进自己的作用域和 try：停机取消照旧 `return`；其余失败记新事件 2156，然后照常跑引擎。
   安全前提是 `RouteGraphAccess.ReadAsync` 会用时钟重判，库里没有 stale 标记的旧快照也会在 `RuntimeStateMaxAge` 之后被判为 stale。
3. 2156 登记进 `scripts/parallel/Test-ParallelInstance.ps1` 的 held 回读活动清单（与 2153 同处）。

## 测试与变异

新测试类 `RouteGraphRefreshTimeoutTests`，6 条：刷新器层 3 条，真 `JourneyRuntimeWorker` 层 3 条。
worker 层用「取货单 FAILED、车还在动」作这一轮必须照常发生的监看，断言同一轮记故障、升级、急停触发一次。

定向测试：`RouteGraphRefreshTimeoutTests`、`RouteGraphEngineTests`、`RouteGraphDispatchTests`，42 条全过（`01-targeted-tests.txt`）。
`Test-ParallelInstance.ps1` 自测 738 passed、0 failed，其中含新加的 2156 一条。

每个变异单独施加、重新构建、跑上面 42 条，然后还原：

| 变异 | 改了什么 | 变红的用例 |
| --- | --- | --- |
| M1 | 刷新器 catch 还原成只认「不是 OCE」 | `ARefreshThatTimesOutIsAFailedRefreshAndDispatchIsBlocked`、`ARoundWhoseRefreshTimesOutStillRunsTheEngine` |
| M2 | 刷新器 catch 吞掉一切 OCE（含停机） | `AShutdownCancellationLeavesTheRefresherUnmarked`、`AShutdownDuringTheRefreshStopsTheWorker` |
| M3 | 刷新器失败时不标 stale | `ARefreshThatTimesOutIsAFailedRefreshAndDispatchIsBlocked`、`ARoundWhoseRefreshTimesOutStillRunsTheEngine`、`RouteGraphEngineTests.ARefreshThatThrowsMarksTheSnapshotStaleInsteadOfLeavingTheOldOneStanding` |
| M4 | worker 刷新失败后跳过引擎（还原成修前的效果） | `ARefreshFailingOutsideTheRefresherStillRunsTheEngine` |
| M5 | worker 刷新的 try 不再放行停机取消 | `AShutdownDuringTheRefreshStopsTheWorker` |
| M6 | `RouteGraphAccess` 去掉按时钟重判 stale | `AnUnmarkedSnapshotStillAgesOutOfDispatch`、`RouteGraphEngineTests.TheRuntimeStateGoingStaleBlocksEvenThoughTheDesignStateIsFine` |

修前代码等于 M1 加 M4 同时施加，两组用例都会红。

一个测量上的坑，写在这里免得重踩：第一次跑 M6 时变异没编译过（`CS9113`，`timeProvider` 未读），而脚本照样跑了测试，
跑的是上一个变异留下的 DLL，结果作废后换了能编译的写法重跑。另外用 `mv` 还原源文件会带回旧的修改时间，MSBuild 会认为不必重编，
所以每次还原后都 `touch` 一下，最后又重新构建、复跑确认 42 条全绿。
