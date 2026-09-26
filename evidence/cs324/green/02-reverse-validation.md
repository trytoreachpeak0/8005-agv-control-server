# cs#324 反向验证（L1）

每次只注入一处，备份还原，`dotnet build --no-incremental` 后 `dotnet test --no-build`，范围是
`StopEndedJourneyContinuesTests` 与 `JourneyRuntimeWorkerLoadCancellationBeforeSublotTests`。注入前先写下预期。

| 注入 | 预期红 | 实际红 |
| --- | --- | --- |
| M1 去掉号数顺延（`WorklistRefills` 不加） | 号数那条；重连补发可能连带 | `AnEndedStop…`、`AfterAReconnect…`、`WhenTheNextStopCarriesNone…`（失败文本：卸货站清单第 3 号没有越过空清单的第 3 号） |
| M2 不暂存空清单 | 空清单、号数、重连、B 形态迟到扫码 | 同预期 4 条 |
| M3 入站不判迟到扫码 | 两条迟到扫码 | 同预期 2 条 |
| M4 取消原因码恒为旧码 | 两条迟到取消 | 同预期 2 条 |
| M5 去掉「阶段仍在等录入就不抢答」 | 护栏那条 | **没有红** |

两处与预期不符，如实记在这里：

- **M1 起初没有让 `TheNextStopsWorklistStartsAboveTheEmptyOne` 变红。**那条用例里甲、乙共用一个卸货站，卸货站把已终结的乙
  也算作「做完」，号自然多出一号，碰巧躲开冲突。所以补了 `WhenTheNextStopCarriesNoneOfTheEndedDemandsItStillStartsAboveTheEmptyWorklist`
  （乙有自己的卸货停靠、终结后被计划删掉），它在 M1 下红在同号上。
- **M5 存活。**处在 `AwaitingSublot` 的停靠按构造总有待做项（进入这一阶段的两处都是「还有没装的」），后面那道「当前停靠无待做项」
  已经挡住，所以这层判断是多余的防御。护栏用例 `ASublotEnteredPastTheDeadlineWhileTheStopStillWaitsIsLeftToTheRuntime` 守的是
  「按期限到没到判结束」这种写法，不是这一行。
