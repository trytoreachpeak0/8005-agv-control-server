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

## 续行只在急停已解开之后才发 CONTINUE（M14–M16）

调度 2026-09-28 转 round-44（`rcs/riot-behavior-lab/evidence/rounds/2026-09-28-round-44`，`BC-ORDER-020`）：锁住期间 RIoT 接受
`CONTINUE_FROM_HELD`、单从 7 变 3（OBSERVED）；解除那一刻车会不会自己走没观测过（第 5 条，INFERRED）。修复是在协调器发 CONTINUE 前
最后一刻再读一次急停。变异条件用 `emergency.IsLatched && !emergency.IsLatched`：常量 `false` 会触发 CS0162（警告当错误），第一次三个都没编过、
一条测试也没跑，已作废重跑。过滤器 `FullyQualifiedName~InTransitDoor|FullyQualifiedName~VehicleFault|FullyQualifiedName~StoppedRebuildExitTests|FullyQualifiedName~EmergencyStop|FullyQualifiedName~EmergencyRelease`（340 条）。

| 编号 | 变异 | 预期红 | 实际 | 退出码 |
| --- | --- | --- | --- | --- |
| M14 | 去掉恢复服务里的闩锁检查（`EmergencyReasons`） | 新用例两格 + 既有的「闩锁」格与理由列表格 | 红 7：`AResumeWhileTheLatchIsStillOnSendsNoContinue`（own、external）、`StoppedRebuildExitTests…(latched)`、`ARefusalIsAConflictNamingEveryReason`、`AResumeIsRefusedForEveryUnmetCriterionIncludingALatch`、`EachCriterionRefusesOnItsOwn(latched)`、`EveryUnmetCriterionIsNamedAndNothingIsChanged`（最后一条没在预期名单里点名，同属核对理由列表的一类）。零 CONTINUE：own 格理由变成 `FAULT_RECOVERY_EMERGENCY_STOP_OPEN`，external 格变成协调器的 `RESUME_EMERGENCY_LATCHED` | 1 |
| M15 | 去掉协调器发 CONTINUE 前的闩锁检查 | 「服务读完后急停又锁上」+ 改写后的 isolation 用例 | 红 2，正是这两条 | 1 |
| M16 | 两处都去掉 | external 格真的发出 CONTINUE | 红 9：M14 的 7 条 + M15 的 2 条；external 格实际理由 `RESUME_CONTINUE_NOT_CONFIRMED`，即 CONTINUE 已发出 | 1 |

**第三道挡，是跑 M16 时才看出来的**：锁住的是本服务端自己发的急停（own 格）时，去掉两处闩锁检查也不发 CONTINUE，因为同一串判断里排在
后面的「本服务端的急停还没关单」接着拒。所以只有 own 一格证明不了闩锁检查有用；external 格（本服务端的急停已解除，之后有人在 RIoT 上按了急停）
才是只剩两处闩锁检查在挡的那一格。

修复前的红：`ALatchThatComesOnAfterTheServiceReadItStillStopsTheContinue` 在只改测试的提交上红，Expected `RESUME_EMERGENCY_LATCHED`、
Actual `RESUME_CONTINUE_NOT_CONFIRMED`（`evidence/cs335/resume-latch-red/dotnet-test.log`）。

## 审查第一路三条必修（M17–M23）

修复提交 `5f7d14e5` 之上。过滤器 `FullyQualifiedName~InTransitDoor|FullyQualifiedName~VehicleFault|FullyQualifiedName~StoppedRebuildExitTests|FullyQualifiedName~EmergencyStop|FullyQualifiedName~EmergencyRelease`。

| 编号 | 变异 | 预期红 | 实际 | 退出码 |
| --- | --- | --- | --- | --- |
| M17 | 续行前不查本服务端还开着的急停（P3） | `AStopReTriggeredAfterTheServiceGateStillStopsTheContinue` | 红 1，正是这条 | 1 |
| M18 | 解除前最后一次读取不核单态（P2） | `AHeldOrderThatRunsBeforeTheLastReadKeepsTheLatch` + 监督器纯函数两格 | 红 3：这条、`held-order-listed-executing`、`held-order-listed-without-state` | 1 |
| M19 | 放行去掉「单已取消或删除」那一支（P1） | 放行纯函数三格 + 必修 1 用例锁着两格 | 红 5，正是这五格 | 1 |
| M20 | 引擎的补充监看直接返回（P1） | 必修 1 用例锁着两格 | 红 2，正是这两格 | 1 |
| M21 | 人工清除不对门锁故障放行被取消的单（P1） | 必修 1 用例三格 + 清除先于引擎那条 | 红 4，正是这四条 | 1 |
| M22 | 门锁故障加被取消单时清除仍走旅程处置（P1） | 同 M21 的四条 | **首轮存活**（绿 361/361，退出码 0）；补断言与用例后红 4，正是这四条 | 0 → 1 |
| M23 | 不点名单的放行不再要求车上没有未完成单（P1） | 监督器纯函数 `no-order-named-one-listed` | 红 1，正是这格 | 1 |

M17、M20、M21 第一次用「参数 is null」一类恒假条件，可空性分析随之把参数当成可能为空，后面用到它就报 CS8602，三个都没编过、
一条测试也没跑，已作废；换成 `cancellationToken.IsCancellationRequested`（测试里恒为假）和一个不存在的证据码重跑，结果如上。

**M22 为什么首轮存活**：重建记录按被终结那张单的单号生成固定编号（`OwnOrderRebuilds.RebuildIdFor`），`StageAsync` 遇到已有的就原样
返回。原用例都先让引擎跑几轮、登记了取消重建再清除，所以清除时即使照 FAILED 那样处置旅程，也拿回同一条记录，看不出差别。
但人按清除可能早于引擎读到这次取消：那时走处置就会先登记一条来源「故障清除」、单态 FAILED 的记录，引擎随后只拿回它——来源和单态
都错，REQ-0361 的重复窗口按来源判也跟着错。补 `AClearanceBeforeTheEngineSeesTheCancellationLeavesTheRebuildToTheEngine`，
并在必修 1 用例里断言处置结果是 `NONE`，M22 变红。

## 两路合并审查补的用例（M18b、M24–M29）

同一过滤器（366 条）。第二路审查的变异 A、B、C、F 与「对调顺序」对应如下。

| 编号 | 变异 | 预期红 | 实际 | 退出码 |
| --- | --- | --- | --- | --- |
| M18b | 同 M18（监督器不核单态），加上新补的 `AHoldConfirmedEarlierThenContinuedInRiotKeepsTheLatch` 重跑 | M18 的三条；新格**存活**（被放行重读挡住） | 红 3，新格绿，与预期一致 | 1 |
| M24 | 变异 A：放行不重读单，直接当作本单 7 | 新格**存活**（被监督器的单态检查挡住） | 新格绿，与预期一致；另红 2：必修 1 用例锁着两格——「单已取消或删除」那一支靠这次重读才看得见终态，伪造成 7 后走不到，被取消单的按住又回查不到确认，放行没有了 | 1 |
| M25 | M24 加 M18 | 新格 + P2 那条 + 纯函数两格 | 红 6：这四条，加必修 1 锁着两格（同 M24） | 1 |
| M26 | 变异 C：放行取按住审计时不限故障代次 | 不确定，照实记 | **存活**（366/366，退出码 0）。见下 | 0 |
| M27 | 变异 F：门锁路径不再排除 HANG | `AHangingOrderIsLeftAloneWhateverTheDoorsSay` 两格 | 红 2，正是这两格 | 1 |
| M28 | 变异 B：续行时读不到急停不拒 | `AnEmergencyStateThatCannotBeReadAfterTheServiceReadItStopsTheContinue` | 红 1，正是这条 | 1 |
| M29 | 对调：先急停、后按住（按住挪进升级那一支、排在急停之后） | 调用 `AssertHeldThenStoppedAsync` 的各格 | 红 13：`ADoorNotProvenLocked…` 全部 6 格与另外两条门锁用例，都红在「the hold must reach RIoT before the emergency stop: triggerEmergency -> OrderHold」；另 5 条 `VehicleFaultIsolationTests` 红在「没有按住」——这个写法让不升级的那条路完全不发按住，它们红的原因与顺序无关 | 1 |

**第 2 条审查要求「变异 A 与『不核单态』各自都让新格红」，按当前代码做不到，这是纵深防御的结果**：审查写这条时（`6db50864`）监督器还不核单态，
变异 A 一去掉重读就放行；必修 2 补上单态检查之后，同一张单变 3 会被两道各自挡住，单独去掉哪一道，另一道都兜得住（M18b、M24），
两道一起去掉才红（M25）。两道各自的有效性另有用例单独钉：放行重读由必修 1 锁着两格（M24 红）钉住，监督器单态由 P2 那条与纯函数两格
（M18b 红）钉住。

**M26 为什么存活**：产品里写 `OrderHold` 审计的只有 `VehicleFaultCoordinator.HoldCurrentOrderAsync`（经 `RiotOrderCommandService.IssueAsync`，
永远带故障代次；另一个调用方 `DemandReleaseService` 发的是取消）。而同一次 `ApplyAsync` 里，本代的按住总在放行读审计之前落库——本代已有
就直接用，没有就先写审计再发。所以放行读到的「这张单最后一条按住」按构造就是本代的，去掉代次过滤在今天的产品里区分不出来。过滤保留，
防的是将来多出第二个写按住的来源；不补用例。

M26、M29 首次写法没编过（M26 触发 CA1826，M29 把语句插进了 `if … else if` 中间），已作废，换写法重跑，结果如上。

## 编号 M5 空缺

本票从未有过编号 M5 的变异：草稿区里没有它的片段文件，也没有运行记录。当初为什么跳过这个编号，会话压缩之后已无从还原。
编号保持空缺，不事后补一个进去。

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
