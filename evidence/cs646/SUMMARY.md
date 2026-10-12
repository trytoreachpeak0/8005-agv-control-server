# cs#646 证据：同站追加时提前结束原停靠的离站等待（带纠错宽限）

本机 Windows，SDK 8.0.425，Debug，基于 `fp/v2-impl` 的 `fc2af82b4`（已含 cs#637 PR #642、cs#643 PR #651）。每次运行都看 `dotnet test` 的退出码，不只看汇总行。

这张票经过三轮：

- 第一版（`18708d80`）只提前结束。审查 S1 指出本站已装需求的纠错机会会**归零**，而且追加早于装货落定时，`AwaitingStationDeparture` 一次都不落库。用户 2026-10-12 定在本票内加一段最短纠错宽限。
- 第二版（`6e217611`）加了宽限。增量审查 S1 指出车载屏上显示的仍是原期限（还剩 4 分多钟），服务端却在宽限到时就送车走。调度 2026-10-12 定在本票内做：下发「实际离站时刻」。
- 本版：判定与下发读同一个「实际离站时刻」；宽限做成可在并行实例定义与 L2 场景里配置。

## 改了什么

### 实际离站时刻

新增 `JourneyRuntimeEngine.EffectiveStationDepartureDeadline(runtime, stops)`。以下全部成立时，它取「起点 + `SameStationAppendCorrectionGrace`」与原期限（`StationDepartureDeadline`）中**较早**的那个；任一条不成立就是原期限：

- 原期限不为空（等待开着、起点已种下）；
- 当前停靠是取货；
- 本停靠已无待装；
- 紧接着的下一个**开着的**停靠是**同一站的取货停靠**。

起点是离站等待的起点：最后一批落定，纠错闭环时挪到闭环那一刻。`SameStationAppendCorrectionGrace` 是新配置项，挂在 `JourneyRuntimeOptions` 里 `StationDepartureWaitTimeout` 旁边，默认 30 秒，不得为负。

读它的有两处，都只在离站等待那一段：

- `StationDepartureWaitIsOverAsync` 的离站判定；
- `RefreshDeadlineOfALoadedStopAsync`：它把期限随一张空清单送到车上。

所以车载屏上的倒计时，就是车真正要走的那一刻。

其余读 `StationDepartureDeadline` 的 8 处保持不变：到站发清单、等录入时的发清单与到期结束、等装货结果、断联重填后的升版、仓门未闭告警、装货结果的回执。它们都发生在当前停靠还有待做项的时候：等录入、在装、刚到站都意味着本停靠有没做完的需求，升版那个函数开头就是「本停靠没有待做项就返回」。这时提前结束的第三个条件不成立，两个值恒等，所以不改。

例外是等录入阶段的到期结束（`TryEndStopAtStationDeadlineAsync`，第三轮审查 S1）：扫码前取消了本站最后一条待录入的需求时，取消那一路不改阶段，旅程留在 `AwaitingSublot`、本站待做项为零，提前结束的条件此时成立，而那里仍按原期限判。车上那张空清单期限为空，屏上显示「无倒计时」，同站追加的新需求要等到原期限才录得进去。车只是等得久，不会意外开动；这条行为在本票之前就有，本票不改，记在 PR 的剩余风险里。

### 为什么这样就堵住了零窗口

装货落定那一轮，起点被置为此刻，转入 `AwaitingStationDeparture`，同一轮判这里。宽限大于零时，实际离站时刻是「此刻 + 宽限」，还没到，于是返回 false，调用方保存。这个阶段必定落库，车载端随即可以发起纠错，服务端在这个阶段授权。宽限为零时退回第一版的行为：落定那一轮就走。

### 纠错闭环之后怎么计时（读 ADR-cross-0055 后定的方案）

ADR-cross-0055 原文：「每个 LoadBatch 或 LoadCorrection 安全闭环……都从完整时长重新计时」。代码里这一句由起点承担：读到闭环的纠错，起点就挪到闭环时刻，原期限 = 闭环 + 完整时长，这一项照旧。

提前结束对这个重新算出的期限与对原期限一样，取较早的那个。理由：

- 完整时长是留给「本站还能继续装货」的，而本停靠已经没有可装的东西；
- 同站的新需求在新停靠到站时，会拿到它自己的完整时长；
- 本停靠要留的只是纠错机会，所以闭环之后再给一段宽限，期间还能再开纠错，开着就挡车。

宽限和期限从同一个起点算，不需要额外的状态。

### 为什么不算改协议语义（调度 2026-10-12 认可）

协议 schema 对 `stationDepartureDeadlineAt` 只规定类型（时刻或空），没有描述文字；这层语义在 ADR-cross-0055 里：「车载端只显示服务端截止时间」。下发的仍是服务端判定本站结束的那一刻，只会比原期限早，不违反「操作员不能暂停或延长期限」。v2 车载端的读法一处不变：显示倒计时、到期后显示「已到期，等待本站结束」、选提示文案。ADR 的核对记录在 program 仓 PR trytoreachpeak0/8005-agv-program#177。

### 配置入口

- `scripts/parallel/ParallelInstance.psm1`：`journeyRuntime` 白名单加 `sameStationAppendCorrectionGrace`。定义里写了就进覆盖层，不写就不写，服务端用自己的默认值。
- `scripts/l2/Invoke-L2Scenario.ps1`：setup 里写了 `SameStationAppendCorrectionGrace` 就传给服务端，不写不传。值由 `L2DispatchZoneParameters.psm1` 的 `Resolve-L2SameStationAppendCorrectionGrace` 读取并校验，与 `CargoHoldingTimeout` 同一个读法。

## 用例

`tests/ControlServer.Tests/SameStationAppendEndsDepartureWaitTests.cs`，14 条，用例里等待 60 s、宽限 10 s。

| 用例 | 钉的是 |
| --- | --- |
| `ASameStationAppendAfterTheGraceSendsTheVehicleOnAtOnceAndTheNewStopGetsTheFullWait` | 宽限已过时同站追加：下一轮就发原停靠的离站核验（原期限还差 30 s 以上）。原地到站后，新停靠起点 = 到站那一轮；车上最新一版清单只列新需求，期限 = 到站 + 60 s |
| `AnAppendBeforeTheLoadCommitsLeavesTheGraceForACorrectionTheServerAuthorizes` | 审查 S1 的零窗口探针：追加早于落定，落定那一轮停在 `AwaitingStationDeparture`（从库里读到），没发核验。宽限内走服务端真实授权路径发起纠错，不被拒，`LoadCorrectionCommand` 进发件箱。宽限过了纠错还开着，车不走，阻断原因 `LOAD_CORRECTION_IN_PROGRESS` |
| `AnAppendBeforeTheLoadCommitsEndsTheWaitWhenTheGraceRunsOut` | 追加早于落定、没人纠错：落定 + 宽限 − 1 s 仍在等，落定 + 宽限那一轮发核验 |
| `ACorrectionClosedInTheGraceEarnsAnotherGraceBeforeTheVehicleIsSentOn` | 宽限内开的纠错闭环：起点 = 闭环时刻、阻断原因清空，车上期限 = 闭环 + 宽限；闭环 + 宽限 − 1 s 仍在等，闭环 + 宽限发核验，早于闭环 + 完整时长 |
| `TheDeadlineOnTheVehicleIsTheMomentTheServerSendsItOn` | 增量审查 S1 的护栏：同站追加后车上最新一版清单（空清单）的期限 = 落定 + 宽限，不是原期限；那一刻之前一秒不发核验，那一刻发。判定与下发只改一边，这条就红 |
| `WithTheGraceAtZeroTheWaitEndsInTheRoundTheLoadCommits` | 宽限为零：退回第一版行为，落定那一轮就发核验 |
| `AGraceLongerThanTheWaitDoesNotExtendIt` | 增量审查 S3：宽限 120 s 比期限 60 s 长，车上期限 = 落定 + 60 s；期限前一秒不发，期限那一轮发 |
| `AnAppendAtAnotherStationStillWaitsOutTheStationDeadline` | 追加在另一站：期限前 2 s 仍不发核验，到期那一轮发 |
| `ASameStationAppendDoesNotEndTheWaitWhileTheCurrentStopHasALoadOutstanding` | 当前停靠还挂着一条待装（库里直接构造，正常流程走不到）：不提前 |
| `ASameStationAppendDoesNotEndTheWaitWhileACorrectionIsOpen` | 纠错开着：不提前，阻断原因是纠错进行中 |
| `ASameStationAppendDoesNotSendAVehicleHeldForItsSlotDoorOn` | 仓门扣车：原期限过了 10 s 也不发核验 |
| `ASameStationUnloadNextDoesNotEndThePickupWait` | 下一个是同站卸货（先装后卸）：照旧等满，归 control-server#647 |
| `ARemovedSameStationStopDoesNotEndTheWait` | 审查 L1 X2：当前停靠与关卡之间夹着一个已删的同站取货停靠（旧版本修订留下的序位，库里直接构造），不算同站追加 |
| `AClosedCorrectionUnderASlotDoorHoldLeavesNoStaleCorrectionReason` | 审查 L1 X4：纠错闭环时车又被仓门扣着，宽限过后不发核验，也不残留 `LOAD_CORRECTION_IN_PROGRESS` |

另有：

- `JourneyRuntimeOptionsTests.SameStationAppendCorrectionGraceDefaultsToThirtySecondsAndIsNeverNegative`（增量审查 S3）：默认 30 秒；0、1 秒、10 分钟都接受，负数拒绝。
- `scripts/parallel/Test-ParallelInstance.ps1` 两条：定义里写了宽限，校验通过、覆盖层带上它；不写时覆盖层不写。
- `scripts/l2/Test-L2DispatchZoneParameters.ps1` 六条：不写不传、10 秒、0 照传、负数拒绝、数字拒绝、不触发批次 7 的拼写近似检查。

不放行的几条，都把钟拨到期限前（或宽限前）一点再跑：只跑一轮、钟不走时，「没发核验」对修前修后都成立，说明不了什么。核验按停靠认（该停靠的 `DepartureSafetyCheckMessageId` 是否进了发件箱），不按条数。纠错闭环是直接把纠错行写成 `Reconciled`（与 `LoadedStopDeadlineReachesVehicleTests` 同一个做法），授权走的是真实路径。

## 变异验证（`mutation/`）

脚本 `mutate3` 一次只做一个变异：

1. 被替换的原文必须恰好命中一处，否则标 `NOT APPLIED`；
2. 构建必须报 `0 Error(s)`，否则标 `BUILD FAILED`，不跑测试；
3. 跑本类加 `JourneyRuntimeOptionsTests`；
4. 按字节写回，并把修改时间改成当前时间。

全部跑完后再构建一次。表的最后一行 `restored build: 0 Error(s)` 就是这一次。每个日志里都有构建那一行 `0 Error(s)`；跑完之后 `git status` 显示 `src` 没有改动。

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| R0 | 实际离站时刻永远取原期限（等于没有提前结束） | 宽限已过同站追加、宽限到才走、闭环再给宽限、宽限为零、护栏 |
| R1 | 引擎整个换回第一版 `18708d80`（有提前结束、没有宽限） | **零窗口探针**、宽限到才走、闭环再给宽限、宽限比期限长、护栏 |
| R2 | 引擎整个换回第二版 `6e217611`（有宽限，但只改判定、下发的仍是原期限） | **护栏**、闭环再给宽限（车上期限那一条断言） |
| M1 | 去掉「同站」比较 | `AnAppendAtAnotherStation…` |
| M2 | 去掉「本停靠已无待装」 | `…WhileTheCurrentStopHasALoadOutstanding` |
| M3 | 纠错闸对「下一个是同站取货」放行（提前结束绕过纠错闸） | `…WhileACorrectionIsOpen`、零窗口探针、`AClosedCorrectionUnderASlotDoorHold…`、闭环再给宽限 |
| M4 | 去掉「下一个停靠是取货」 | `ASameStationUnloadNext…` |
| M5 | 去掉「当前停靠是取货」 | **无（等价变异）**。卸货停靠在 `StationDepartureWaitIsOverAsync` 的上一支已先返回 `true`，刷新那一处一开头就排除了非取货停靠。剩下的角色只有 `CHARGER`：充电停靠由充电那几支（`JourneyRuntimeEngine.Charging.cs` 等）推进，读代码看不到它经装货落定进入离站等待的路，所以没有构造（推的，未实测）。保留这一项，是让条件自己读得懂 |
| M6 | 删掉调用方的仓门扣车闸 | `…HeldForItsSlotDoorOn`、`AClosedCorrectionUnderASlotDoorHold…` |
| M7 | 去掉宽限（提前时刻 = 起点） | 零窗口探针、宽限到才走、闭环再给宽限、宽限比期限长、护栏 |
| MC | 不与原期限取较早者（宽限比期限长时会延长） | `AGraceLongerThanTheWaitDoesNotExtendIt` |
| MS | 只改下发：刷新那一处退回读原期限 | 护栏、闭环再给宽限 |
| MJ | 只改判定：离站判定退回读原期限 | 宽限已过同站追加、宽限到才走、闭环再给宽限、宽限为零、护栏 |
| X2 | `OpenStops` 换成 `Stops`（审查 L1） | `ARemovedSameStationStopDoesNotEndTheWait` |
| X4 | 删掉起点更新那一块里的清阻断原因（审查 L1） | `AClosedCorrectionUnderASlotDoorHold…`、闭环再给宽限 |
| ME | 删掉宽限负数校验（增量审查 S3） | `JourneyRuntimeOptionsTests.SameStationAppendCorrectionGraceDefaultsToThirtySecondsAndIsNeverNegative` |
| SW | 白名单里删掉 `sameStationAppendCorrectionGrace` | `Test-ParallelInstance.ps1`「correction grace: a definition that sets it is accepted…」，759 过 1 失败，退出码 1 |
| SL | 删掉 L2 读取函数里的负数检查 | `Test-L2DispatchZoneParameters.ps1`「a negative grace is refused」，退出码 1 |

SW、SL 两项是手工做的：改一处、跑脚本自测、`git checkout` 还原那一个文件（改动先提交过）。

第二版那一轮的教训留在这里：那一轮第一次跑 R1 时，脚本把旧文件读坏了（编码），构建报了 251 个错误，测试跑的是上一个变异留下的二进制，红集与 R0 一模一样。从那以后，脚本在构建失败时直接跳过。

## 定向回归

改离站阶段、途中追加或调用 `ZeroChangePin` 的测试类，加 `ArchitectureTests` 与 `JourneyRuntimeOptionsTests`（过滤条件见 `regression-filter.txt`）：`Passed: 1688, Failed: 0`，退出码 0（见 `regression.txt`）。上一版是 1685 条，本版新增 3 条（护栏、宽限比期限长、配置负数），对得上。全量交给 CI。

脚本自测：`Test-ParallelInstance.ps1` 760 过 0 失败，`Test-L2DispatchZoneParameters.ps1` 全部通过，两个退出码都是 0。

## 车载端（只读，`w2g/fp-v2-impl` 的 `27ecf24`）

**新需求能不能立即显示（第 4 步）。**新停靠的清单与录入请求，带的是新停靠自己的作业会话号、同一个站号：

- 车载端 `WireToGateSessionClient` 收到清单，按修订号整版替换，不比站号；
- `WireToGateBusinessService.IsStopOf` / `EndsStopOf` 按「会话号 + 站号」配对，新会话号的清单与新会话号的录入请求互相认得；
- 旧停靠的录入请求早已被装货命令撤销（`WithdrawEntryRequestAnsweredBy`）；
- 车载端收到离站核验只作答，不进入任何锁界面的状态；
- 扫码入口 `CanSubmitSublot` 另要求会话 Ready 与车停稳。原地「到站」车不动，满足。

结论：新停靠开放后，车载屏能立即显示新需求、接受扫码，**不需要改车载端**（读代码得出，没在真装置上跑）。

**纠错入口（审查 S1，读到的）。**车载端只对「最近一次完成的装货」开纠错入口：`WireToGateBusinessService.RecoveryVectors.cs` 的 `CanRequestLoadCorrection`，以及取 `LastCompletedLoadOperationContext` 的 `RequestLoadCorrectionCoreAsync`。宽限期内，最近一次完成的装货就是本站这一条，入口开着；新停靠一装完，入口就换成新需求，本站已装的那几条在车载端再也纠正不了。

**纠错进行中收到离站核验怎么答（增量审查 L2）。**

- 读到的：`HandlePreDepartureSafetyCheckAsync` 的结论只取自 `EvaluateSafety`，也就是 `WireToGateSafetyEvaluator.Evaluate`：IO 新鲜、仓门全锁、开锁输出全复位、车停稳、没有严重故障闩锁，五样都满足就答 `SAFE`。这里没有任何一处看「有没有纠错正在进行」。
- 读到的：命令按到达顺序在同一个 `switch` 里分派（`WireToGateBusinessService.cs` 约 2520–2545 行，`LoadCorrectionCommand` 与 `PreDepartureSafetyCheck` 是同一个 `switch` 的两个分支）。
- 推的：如果纠错命令执行期间仓门被打开，那时收到的核验会答 `UNSAFE`（门没锁）。如果核验在纠错命令开门之前、或门关好之后被处理，就会答 `SAFE`。纠错处理是在开门、关门全部结束之后才返回，还是先返回再在后台跑，没有读到底。

## 转 ready 那一次的推送事故（2026-10-12）

第三轮审查后，我把 `git push ... | tail -1;`、`gh pr edit`、`gh pr ready` 写在一条命令里。push 撞上网络错误失败了（`schannel: server closed abruptly (missing close_notify)`），但管道吞掉了 push 的退出码，分号又让后面几步照跑：#660 和 trytoreachpeak0/8005-agv-program#177 都转了 ready。#660 那一轮 CI 因此跑在旧 head `26853af7` 上，而最终 head 是只改了注释与本摘要的那一个提交。同一时刻，l2 的「Decide whether this pull request runs the L2 round」也因为 runner 访问 GitHub 撞上同一个错误而 exit 1，那不是产品红。

调度定：再推最终 head、多跑一轮，旧那一轮 test 让它跑完、不取消。教训：push 单独执行，不和别的命令用分号或管道串起来；推完用 `gh pr view` 回读 `headRefOid`，等于本地 HEAD 之后才转 ready。
