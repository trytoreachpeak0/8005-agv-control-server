# 红证据：在途车根本到不了派车轮次

这是 `multi-stop-append-same-zone` 在修好之前的一次运行（批次7-06，control-server#211）。

## 现象

`assertions.json` 停在 `L2-MSA-03`：第二条需求没有进那趟正在跑的旅程，归属数一直是 1。
`snapshots/db-JourneyBacklog.json` 里**只有第一条需求那一行**——第二条连积压行都没有，
也就是说轮次根本没见过它，而不是见过之后拒绝了它。

## 原因

`JourneyRuntimeEngine.ExecuteOnceAsync` 里有一句 `if (free.Length == 0) return;`：没有空闲车就
提前结束，目录不读、孤儿检查也不跑。那在本票之前是对的——在途车走一条一律拒绝的占位路径
（`InTransitAppendNotOpened`），问它等于白问。本票让在途车与空闲车在同一张候选表上竞争
（REQ-0205），这句判断就必须跟着改成「一辆车都没有才退」。

**单车现场里这不是边角情形，而是常态**：车一接单就不再空闲，此后到卸完货为止的每一条新需求都
只能靠追加接。按空闲车判，这些需求一条都看不见。

## 为什么 L1 没有抓到

`MultiVehicleExecutionTests` 的夹具是三辆车，测「在途车」的用例里总还有空闲车在，那句提前退出
因此从来没有被触发过。只有「车队里每一辆都在途」才踩得到，而那正是单车现场的日常。

## 修在哪

`src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs`，判断改为
`if (free.Length == 0 && underWay.Length == 0)`。回归用例是
`WithEveryVehicleUnderWayTheRoundStillReadsTheCatalogAndReports` 与
`WithEveryVehicleUnderWayTheOrphanCheckStillGuardsIntake`（两条都从原来那条反过来的基线改写而来）。
绿的那一次在 `../b7-06-append-same-zone/`。
