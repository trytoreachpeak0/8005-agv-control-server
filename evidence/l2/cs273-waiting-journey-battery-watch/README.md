# cs#273 证据：旅程等人期间的电量监看

这个目录只保留摘要。完整日志（服务端控制台日志 0.96 MB 等）没有入库，每一轮 CI 都会重新产出一份。

| 文件 | 内容 |
| --- | --- |
| `SUMMARY.md`、`assertions.json` | 合成 L2 场景 `waiting-journey-battery-watch` 在本地跑的一次，结果 PASS。服务端提交 `7fcdc6cf`，就是加入这个场景的那个提交 |
| `SUMMARY-mutant-engine-skips-watch.md` | 同一提交上，把引擎里 `.ObserveAsync(active, …)` 改成 `.ObserveAsync([], …)`（引擎不再调用监看）之后再跑一次，结果 **FAIL**：`Timed out after 60s waiting for: the server logged the wait with the battery under the rescue line`。说明这个场景能区分「监看在跑」和「监看没跑」。跑完后按备份还原 |

## 单元用例的反向验证（变异）

变异在提交 `59d34788` 上跑，筛选条件是 `FullyQualifiedName~WaitingJourney`，当时共 30 条用例。做法是每次只改一处，改完先确认 `0 Error(s)`（编译确实通过），再跑用例，跑完按备份还原。十个变异全部出现红：

| 变异 | 改了什么 | 红的用例数 | 红的是哪几条 |
| --- | --- | --- | --- |
| M01 | 保存时不再给 `StageSince` 盖章 | 15 | 所有依赖「已等多久」的用例、阶段起点盖章用例、库拒写用例 |
| M02 | 读电量抛出的异常不再兜住 | 1 | `AFailedBatteryReadIsLoggedAsUnknownAndNeverHoldsTheJourneyBack` |
| M03 | 监看自身的失败不再兜住 | 1 | `AWatchWriteTheDatabaseRefusesNeitherEndsTheRoundNorTouchesTheJourney` |
| M04 | 闸口等卸货改为算作「在路上」 | 5 | 闸口用例、读失败用例、分类穷举表、看板两条 |
| M05 | 去掉重复间隔 | 1 | `TheWaitIsLoggedAgainOnceEveryRepeatIntervalAndARestartNeitherRepeatsNorLosesTheCadence` |
| M06 | 写库后不把被跟踪的那一行对齐 | 1 | 同上（同一个上下文里下一轮会误以为还没报过） |
| M07 | 救命线改成含等号 | 1 | `TheLevelFollowsTheDispatchMinimumAndTheRescueLine(battery: 15 …)` |
| M08 | 看板的已等时长改为从 `UpdatedAt` 算 | 2 | 看板端点用例、卡片用例 |
| M09 | 引擎不调用监看 | 12 | 所有走引擎的监看用例 |
| M10 | 在路上一律算等人 | 2 | `AJourneyUnderWayOnALegIsNotAWaitHoweverLongItTakes`、看板端点用例 |
