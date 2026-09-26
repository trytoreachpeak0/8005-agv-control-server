# cs#362 反向验证

在修复之上逐个注入变异，`dotnet build --no-incremental` 后只跑 `CancelledDemandLoadCommandTests`。脚本 `02-reverse-validation-mutations.py`：
源码先备份，替换匹配数不为 1 就不跑，跑完按备份还原；原始输出 `02-reverse-validation-raw.txt`（三轮，见下）。
变异用 `runtime.AgvId.Length < 0` 顶替条件：`if (false)` 触发不可达代码告警（按错误处理），`entered is null` 触发空值分析，两种都编译不过、什么都没测。

复核有四条：旅程仍在等录入（下称「阶段」）、需求仍是 `Accepted`（「需求」）、归属仍是游标读到的那个（「归属」）、本站没有开着的扫码前取消。

注入之前写下的预期：去掉整个复核时每一条都红，四格各红在自己的后果上（「装上了」红在货留车上）；单去一条都存活，因为已知的两种写法各被
不止一条挡住——终结（`PickupStopTermination.StageDemandTerminationAsync`）同写需求与归属，阶段不动；证明不了空的取消结果
（`OnboardRecoveryCoordinator.KeepDemandAndJourneyBlockedAsync`）只写需求，同时把旅程转 `Blocked`、归属不动。

| 变异 | 去掉的复核 | 结果（第三轮，6 条） |
| --- | --- | --- |
| R1 | 整个复核 | 6/6 红 |
| R2 | 需求 | 全绿（存活） |
| R3 | 归属 | 全绿（存活） |
| R4 | 阶段 | 全绿（存活） |
| R23 | 需求 + 归属 | 5 红 1 绿：终结那五条红；证明不了空那条由阶段挡住 |
| R24 | 需求 + 阶段 | 1 红 5 绿：证明不了空那条红（只剩归属，而那条路不写归属）；终结五条由归属挡住 |

所以单独存活的 R2、R3、R4 不是多余代码：终结靠「需求或归属」、证明不了空靠「需求或阶段」，每条复核都是至少一种写法的唯一剩余防线之一。

R1 下每一条红在哪（读到的）：

| 用例 | 跑到底之后 |
| --- | --- |
| 装上了 | 乙归属 `LOADED`、装货 `Committed`、旅程 `Completed`、完成记录只有甲——**货留在车上**，乙记着 `Cancelled` |
| 期限前报空 | 乙需求 `RecoveryRequired`（从 `Cancelled` 被改写）、装货 `RecoveryRequired`、旅程 `Blocked` |
| 期限后报空 | 乙归属 `LOADING`、装货 `Failed`、旅程停在 `AwaitingLoadResult` |
| 装货中按取消 | 乙归属 `LOADING`、装货 `Prepared`、旅程停在 `AwaitingLoadResult`（在途取消被拒） |
| 本站还有丙 | 「已取消的乙被下了装货命令」 |
| 取消证明不了空 | 「证明不了空的取消之后，乙仍被下了装货命令」 |

三轮的来由：第一轮「本站还有丙」红在一句写成「前提」的归属断言上——那句断言本身就会被缺陷改写，不是真前提；改成「这一刻还没有人答这条录入」
后第二轮重跑，它红在「下了装货命令」上。第三轮是审查终结写法清单时发现「证明不了空」这条只写需求的路，补了用例，再加 R4、R24。
