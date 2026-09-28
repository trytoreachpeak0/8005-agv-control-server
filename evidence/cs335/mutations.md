# control-server#335 变异反向验证

每个变异只改一处（替换必须恰好命中 1 处，否则脚本退出、什么都不改），`dotnet build --no-incremental` 全量重编，
跑 `FullyQualifiedName~InTransitDoor|FullyQualifiedName~OnboardSessionLostBlockTests`，记录退出码与红的用例，
再用备份还原。预期写在跑之前（PR 正文与会话记录里有同一张表）。脚本：会话 scratchpad 的 `mutate.py`，逻辑见本文件末尾。

基线：提交 `095ba77f` 加上本提交里对 `InTransitDoorEmergencyReleaseTests` 的加强（车停在两站之间，见 M8）。

## 第一轮（基线 `095ba77f`）

| 编号 | 变异 | 预期红 | 实际 | 退出码 |
| --- | --- | --- | --- | --- |
| M1 | 放行从「只认本代自己确认过的 7」放宽成「任何 7」（去掉按住已确认与 orderId 相同两条） | 纯函数 4 格 | 红 4：`hold-failed`、`hold-pending`、`no-hold-this-generation`、`other-order-id-paused` | 1 |
| M2 | 放行从「只认门锁症状」放宽成「任何症状」（纯函数里去掉证据码判断） | 纯函数 `order-failed` | 红 1：`order-failed` | 1 |
| M3 | 失联也触发门锁症状（`ReportedNotLocked` → `NotProvenLocked`，即选项 Y） | 本类 3 条 + cs#234 那组 | 红 11：本类 3 条 + `OnboardSessionLostBlockTests` 8 条 | 1 |
| M4 | 把 `unknownPresent` 当作仓位状态未知 | 「正常行驶不触发」2 条 | 红 2：`ANormalDriveOnTheOwnOrderNeverHoldsOrStops`（pickup、gate） | 1 |
| M6 | 去掉门锁事实的新鲜度判断 | 「过期的锁闭摘要不解除」 | 红 1：`DoorsNotProvenLockedKeepTheLatch(stale)` | 1 |
| M7 | 在引擎里加第二个 `RequestStopAsync` 调用方 | 单调用方护栏 | 红 1：`OnlyTheFaultCoordinatorRequestsAnEmergencyStop` | 1 |
| M8 | 门锁原因解除后不再豁免「报不出站点」（改回 `WasReleasedOnConfirmationAsync`） | 整条路那一条 | **首轮存活**（绿 51/51，退出码 0）；加强测试后红 1：整条路那一条，红在第 80 行「急停次数期望 1、实际 2」 | 0 → 1 |
| M9 | 门锁故障只在门锁读数不对时交给故障模型（去掉「故障在效」这一半） | 失联时监看照常 + 整条路 | 红 2：`ARaisedDoorFaultIsStillSupervisedEveryRoundWhileTheSessionIsSilent`、整条路那一条 | 1 |
| M10 | 监督器放行时不管车上还有没有别的未完成单 | 引擎格 + 纯函数格 | 红 2：`AnotherUnfinishedOrderOnTheVehicleKeepsTheLatch`、纯函数 `another-unfinished-order` | 1 |

## 最终代码上重跑（基线 `057cdf40`，M1–M13）

门锁豁免收窄（调度 2026-09-28：只免「报不出站点」、只在本代门锁自动解除后生效）与 `HELD_ORDER_RESUMED_WITHOUT_CONTINUE` 之后，
M1–M13 在同一份最终代码上整批重跑，过滤器改为 `FullyQualifiedName~InTransitDoor|FullyQualifiedName~OnboardSessionLostBlockTests`（59 条）。
M1–M7、M10 的红与第一轮相同；M8、M9 因为多了豁免边界那几条而红得更多，都是预期之内的格子。

| 编号 | 变异 | 预期红 | 实际 | 退出码 |
| --- | --- | --- | --- | --- |
| M1 | 同上 | 纯函数 4 格 | 红 4，同第一轮 | 1 |
| M2 | 同上 | 纯函数 `order-failed` | 红 1，同第一轮 | 1 |
| M3 | 同上 | 本类 3 条 + cs#234 8 条 | 红 11，同第一轮 | 1 |
| M4 | 同上 | 正常行驶 2 条 | 红 2，同第一轮 | 1 |
| M6 | 同上 | `DoorsNotProvenLockedKeepTheLatch(stale)` | 红 1，同第一轮 | 1 |
| M7 | 同上 | 单调用方护栏 | 红 1，同第一轮 | 1 |
| M8 | 解除后不再豁免（`releasedOnConfirmation \|\| (releasedOnDoorCause && context.DoorCauseRemoved)` → `releasedOnConfirmation`） | 整条路 + 依赖解除后状态的各格 | 红 8：整条路、`AfterTheDoorRelease…` 5 格、`TheExemptionEndsWithTheFaultGeneration`、`AHeldOrderThatRunsAgainWithoutAContinueIsNamed`（后几条的前置 `ReleasedForTheDoorsAsync` 断「急停一次」，没有豁免就在前置里红） | 1 |
| M9 | 同上 | 失联监看 + 整条路及依赖解除后状态的各格 | 红 9：M8 那 8 条 + `ARaisedDoorFaultIsStillSupervisedEveryRoundWhileTheSessionIsSilent` | 1 |
| M10 | 同上 | 引擎格 + 纯函数格 | 红 2，同第一轮 | 1 |
| M11 | 豁免不看此刻门锁（`(releasedOnDoorCause && context.DoorCauseRemoved)` → `releasedOnDoorCause`） | `doors-not-locked-again`、`slot-state-unknown-again` | 红 2，正是这两格 | 1 |
| M12 | 豁免不限代次（`WasReleasedOnDoorCauseAsync` 去掉 `release.FaultGeneration == faultGeneration`） | `TheExemptionEndsWithTheFaultGeneration` | **存活**（绿 59/59，退出码 0）；补用例后红 1：`TheExemptionEndsWithTheFaultGenerationEvenOnceTheDoorsAreLockedAgain`，「急停次数期望 2、实际 1」 | 0 → 1 |
| M13 | 去掉「单被跑起来而没人按继续」的命名（条件前加 `false &&`） | `AHeldOrderThatRunsAgainWithoutAContinueIsNamed` | 红 1，正是这条 | 1 |

M12 补测后那一次单独重跑，过滤器是 `FullyQualifiedName~InTransitDoorEmergencyReleaseTests|FullyQualifiedName~InTransitDoorLockFaultTests`（50 条），红 1、其余 49 绿。

## M12 为什么首轮存活

预期红的那条用例里，第二代故障是由「门又报没锁」立起来的。那一轮 `DoorCauseRemoved` 为假，M11 守的那个条件已经把豁免关掉，
代次条件无论在不在，结果都一样——两个条件叠在同一格上，只能证明它们合起来有用，证明不了代次条件自己有用。
补的用例把两者分开：第二代故障先在站点上立起（位置已知、停稳，只按住不急停），随后门锁恢复为锁闭、车报不出站点。
这时「此刻门锁锁闭」成立，只剩代次条件在挡；正确实现下本代还没自己解除过，照常急停（第 2 次），M12 借用上一代的解除记录豁免掉，只有 1 次。

## M8 为什么首轮存活

夹具的车默认报站点 1（`RecordingRiot` 构造时 `CurrentStationId: 1`），`VehicleMotionSample.HasKnownPosition` 恒为真，
「解除后不因报不出站点再急停」这条豁免在测试里从来没被用上。真实 RIoT 在两站之间报站点 0（ADR-cross-0060），正是这条豁免存在的理由。
所以不是代码多余，是测试没摆到那个位置。修法：`LatchedForTheDoorsAsync` 从行驶起让车报站点 0。加强后正常实现 41/41 绿，M8 红。

## M2 在引擎层没有红格，这是结构保证

订单 FAILED 那条路（`ObserveOrderFailureAsync`）交给故障模型的上下文从不带 `DoorCauseRemoved`，默认 false，
`DoorReleaseAllowanceAsync` 在第一行就返回空。所以「放宽成任何症状」在引擎层走不到放行；能抓住它的只有纯函数那一格。
引擎层的 `AFailedOrderFaultIsNotReleasedAutomaticallyWhateverTheDoorsSay` 守的是这条结构前提本身。

## 脚本逻辑

读目标文件，`old` 片段必须恰好出现 1 次；复制备份；写入替换后的内容；`dotnet build ... --no-incremental`，编译失败即停；
`dotnet test ... --no-build --filter <filter>`，从日志取 `Failed ControlServer.*` 行与汇总行；`finally` 里用备份覆盖回去。
还原保留旧的修改时间，所以下一次编译一律 `--no-incremental`。
