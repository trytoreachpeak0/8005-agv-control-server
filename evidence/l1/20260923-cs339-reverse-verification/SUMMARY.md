# control-server#339 反向验证

在 `c2913951` 与 `6013c0da` 上（后者只多了 L2 脚本，产品代码与用例相同），用 `mutate.py` 每次改产品代码的一处、构建、跑
`RefilledStationDeadlineReachesVehicleTests` 与 `ArrivalPublishInterruptedThenReconnectedTests` 两个类（共 30 条），跑完按原字节还原。
每处替换都先断言源码里恰好命中一次，没命中就不跑（`matches=[1]` 写在每个文件第一行）。各文件留的是 diff、错误信息与汇总行。

| 变异 | 改了什么 | 变红的用例 |
| --- | --- | --- |
| R1 | 期限不一致也不升版（`AdvanceWorklistPastAStaleDeadlineAsync` 恒返回空） | 8 条：等录入、装货中、真车载端形状三条回到「车上仍是旧期限」；到站重跑三条（本类一条、cs#331 类两条）变成每轮 `ProtocolContentConflictException`，因为沿用已不再忽略期限；修订号那两条 |
| R1b | R1 再加上把到站那一张清单的沿用改回「忽略期限」（即 cs#331 合入时的样子） | 7 条：到站重跑那条回到「车上仍是旧期限」（`01:00:30` 对 `01:00:37`） |
| R2 | 修订号不算重填次数 | 8 条 |
| R3 | 版本数不算重填次数（录入地址区间、后续停靠首号） | 2 条：`AfterARefillTheJourneyReachesTheGateWithTheWorklistRevisionStrictlyAdvancing` 两行 |
| R4 | 「到站那一张已被取代」恒真 | 19 条，含 `AnUninterruptedArrivalStillSendsItsBusinessStateAndTheVehicleConfirmsIt` 两行 |
| R5 | 「车已被告知到站」只看阶段 | 1 条：持货探针（车拒收 `VehicleBusinessStateSnapshot` 修订号 1 两次） |
| R6 | 「到站那一张已被取代」恒假 | 1 条：持货探针（重跑那一轮失败） |
| R7 | 沿用只看「已确认」、不比内容（`OnboardJourneyPublisher.QueueEnvelopeAsync`） | 1 条：`AnAcknowledgedWorklistThatDiffersBeyondItsDeadlineIsStillRefusedAndTheBoardSaysSo` |

R1 第一次跑时变异写成 `if (true)`，编译器报 CS0162（不可达代码）没跑成；R4、R6 第一次写成 `Task.FromResult(true/false)`，报 CA1822。
三处都改写成编译器看不穿的形式后重跑，这里留的是重跑那一次。

**R4 为什么红 19 条（审查低优先级项，2026-09-24 补记，读 `R4-superseded-always-true.log` 数出来的）。**「到站那一张已被取代」恒真，等于到站时
永远不发车辆业务状态，所以凡是走过一次到站、并断言到站发了哪几类快照的用例都红：17 条是同一个断言——到站发出的快照类型期望
`["CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "VehicleBusinessStateSnapshot"]`、实际少了最后一类；另 2 条是
`AnUninterruptedArrivalStillSendsItsBusinessStateAndTheVehicleConfirmsIt` 两行，按到站那一张的 messageId 查发件箱，`Sequence contains no elements`。
预期就是「每条经过到站的用例都红」，数目大不是因为变异打偏了，而是这条判据在每次到站上都起作用。
