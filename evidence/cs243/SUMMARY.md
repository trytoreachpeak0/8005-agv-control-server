# cs#243 证据：挂起钩子的有界上限

票：https://github.com/trytoreachpeak0/8005-agv-control-server/issues/243

本机（控制端笔记本）跑，`dotnet test -c Release --filter`，过滤串见 `filter10.txt`：用到五个挂起钩子的全部十条用例
（`FleetRiot.HangOn` 3 条、`FleetCatalog.HangOnRead` 1 条、`FleetBoxCounts.HangOnCall` 3 条、
`RecordingAcceptances.HangBeforeFirstAccept`／`HangAfterFirstAccept` 3 条）。

本机跑时可用内存只有 0.5～1.3 GiB（总 15.31 GiB，其他会话占用），耗时偏长，所以单条耗时与全绿的最终判断以 PR 那一轮 CI 的 `test-results.trx` 为准。

## green/：改后代码（`c8ee0c6b8`）

10 条中 9 条通过。1 秒预算的用例约 1.1～1.5 s，3 秒预算的约 3.1 s，宿主关停那条 13.5 s、空闲返回那条 14.6 s（含夹具建库开销），都在 60 s 以下，说明结束它们的是预算或宿主取消，不是 `HangCeiling`。

失败的 1 条是 `WhatAVehicleCutOffMidChainHadStagedIsNotSavedWithTheNextVehicle`（32.7 s）：

```
Assert.Equal() Failure: Collections differ at index 0
Expected: "老厂前线新多仓位2"
Actual:   "老厂前线新多仓位3"
```

这条在本机与本票改动无关：未改动的 `fp/v2-impl@da6e40e2` 上本机同样失败，形状相同（第 2 辆车跑满默认 30 秒预算）；而 CI 在同一个 `da6e40e2` 上是绿的（run 38103884151）。已报调度。

## red/：注入「取消失效」后的三种情形

三种注入都把 `HangAsync` 里的 `Task.Delay(HangCeiling, cancellationToken)` 换成 `CancellationToken.None`，模拟「预算或关停取消坏了、钩子收不到取消」（票面担心的「把 `CancellationToken.None` 传到这一层」）。各目录的 `injection.diff` 是注入的确切内容，均未提交。

### injected-ceiling-reached/：本票最终形状 + 取消失效 → 全部变红

10 条全部 Failed。其中 9 条在 60.1～79.2 s 失败，失败原文都是：

```
Hang hook <钩子名> was not cancelled within HangCeiling (60 s): the budget or shutdown the test meant to end it did not (control-server#243).
```

栈都落在 `FleetFixture.DisposeAsync` 的释放检查上。剩下 1 条是上面那条本机本来就坏的 `WhatAVehicleCutOffMidChainHadStagedIsNotSavedWithTheNextVehicle`，1.4 s 时以 `Assert.NotEmpty() Failure: Collection was empty` 失败，没走到上限，所以它不构成本票的红证据。

### variant-returns-normally/：改动前的形状（到点正常返回）+ 取消失效 → 静默变绿

单跑 `AVehicleThatTimedOutIsServedAgainInTheNextRound`：**Passed，74.5 s**。钩子到点当作调用成功返回，被挂的车在一分钟后被服务，而这正是该用例断言的结果。这就是审查指出的问题：预算坏了，用例反而变绿。

### variant-throw-without-dispose-check/：到点抛异常、但关掉夹具释放检查 + 取消失效 → 仍然静默变绿

单跑同一条：**Passed，76.2 s**。`DispatchRoundRunner` 对单车段里非宿主取消的异常一律记为「该车本轮未服务」然后继续，结果与预算截断相同。所以只抛异常不够，必须由夹具在释放时检查「有没有钩子到点」。

（这一目录放在 `red/` 下，是因为它属于注入后的对照；它的结果是绿，证明的是「弱一档的做法会漏」。）
