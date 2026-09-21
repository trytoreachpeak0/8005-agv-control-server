# 红证据：追加改写了计划，但车上那一版没有被替换

这是 `multi-stop-append-same-zone` 在修好之前的又一次运行（批次7-06，control-server#211）。
上一个红证据（`../b7-06-red-01-under-way-vehicle-never-reaches-the-round/`）修完之后，追加本身成立了，
这一条卡在它的下一步。

## 现象

`assertions.json` 停在 `L2-MSA-08`：车上最后一版计划只有两条腿。而库里的停靠确实已经是三个
（运行目录保留的 `controlserver.db` 里 `JourneyStops` 有 12 取货、11 取货、210 卸货三行），
发件箱里的 `UpcomingStopPlanSnapshot` 却只有一条，修订号 1、两条腿、已确认。

**车手里拿着的是插入之前的那张计划**，而 ADR-cross-0053 要求计划整体替换下发。

## 原因

`RefreshUpcomingStopPlanAsync` 先要找到「车上最后一版计划」才能判断该不该重发，而它是用
消息 id 推算出来的：假定第一版的 id 就是停靠行上的 `PlanMessageId`。实际上**一张计划的第一版由三个
地方发出，各有各的 id 来源**——派往取货站那一版按锚需求算（`PickupDispatchPlanMessageId`），
到站那两版才用停靠行上的那一个。推算写漏了第一种，于是「最后一版」找不到，方法直接返回，
什么都不做。

**静默地什么都不做，是最难发现的那种错**：没有异常、没有日志、单元测试全绿，只有把两端接起来
跑一次才看得见。

## 修在哪

`src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs`：`LastSentPlanAsync` 改成**从发件箱读**
最新的一条 `UpcomingStopPlanSnapshot`（按载荷里的 `agvId` 筛），不再推算 id。重发那一版的 id
仍然按停靠与修订号算，因为它只由这一处产生。

同时加了一道前置：只有带着多于一条需求的旅程才走这一段。单需求旅程——今天现场跑的全部——
因此一步都不进，既省掉每个 tick 一次发件箱读，也让这次改动在单需求那条路上完全不执行。

绿的那一次在 `../b7-06-append-same-zone/`。
