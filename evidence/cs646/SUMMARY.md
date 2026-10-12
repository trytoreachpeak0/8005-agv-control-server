# cs#646 证据：同站追加时提前结束原停靠的离站等待（带纠错宽限）

本机 Windows，SDK 8.0.425，Debug，基于 `fp/v2-impl` 的 `fc2af82b4`（已含 cs#637 PR #642、cs#643 PR #651）。每次运行都看 `dotnet test` 的退出码，不只看汇总行。

第一版（`18708d80`）在审查里被指出 S1：提前结束会让本站已装需求的纠错机会**归零**，而且追加早于装货落定时，`AwaitingStationDeparture`
一次都不落库。用户 2026-10-12 定在本票内加一段最短纠错宽限。本文件是加宽限之后的版本。

## 改了什么

`JourneyRuntimeEngine.StationDepartureWaitIsOverAsync` 在纠错闸、卸货停靠那一支、起点与纠错闭环的计算之后，期限判定之前，加一个条件。以下全部成立时返回「等待已结束」：

- 当前停靠是取货；
- 本停靠已无待装；
- 紧接着的下一个**开着的**停靠是**同一站的取货停靠**；
- 离站等待的起点（最后一批落定，纠错闭环时挪到闭环那一刻）之后已过 `SameStationAppendCorrectionGrace`（新配置项，挂在 `JourneyRuntimeOptions` 里 `StationDepartureWaitTimeout` 旁边，默认 30 秒，不得为负）。

只截短不延长：宽限没到时照原期限判；等待关着（期限为空）时照旧当轮离站。持货等单与仓门扣车在调用方照判；离站核验与原地「到站」流程不变。

### 为什么这样就堵住了零窗口

装货落定那一轮把起点置为此刻、转入 `AwaitingStationDeparture`，然后同一轮判这里。宽限大于零时「此刻 ≥ 此刻 + 宽限」不成立，期限也没到，于是返回 false，
调用方保存——这个阶段必定落库，车载端随即可以发起纠错、服务端在这个阶段授权。宽限为零时退回第一版的行为（落定那一轮就走）。

### 纠错闭环之后怎么计时（读 ADR-cross-0055 后定的方案）

ADR-cross-0055：「每个 LoadBatch 或 LoadCorrection 安全闭环……都从完整时长重新计时」。代码里这一句由起点承担：读到闭环的纠错，起点挪到闭环时刻，期限 = 闭环 + 完整时长，照旧。
提前结束对这个重新算出的期限与对原期限一样：宽限过后把它截短。理由是完整时长留给「本站还能继续装货」，而本停靠已无可装的东西，同站的新需求在新停靠到站时会拿到它自己的完整时长；
本停靠要留的只是纠错机会，所以闭环之后再给一段宽限，期间还能再开纠错，开着就挡车。宽限从同一个起点算，所以这一条不需要额外的状态。

## 用例（`tests/ControlServer.Tests/SameStationAppendEndsDepartureWaitTests.cs`，12 条）

用例里等待 60 s、宽限 10 s。

| 用例 | 钉的是 |
| --- | --- |
| `ASameStationAppendAfterTheGraceSendsTheVehicleOnAtOnceAndTheNewStopGetsTheFullWait` | 宽限已过时同站追加：下一轮就发原停靠的离站核验（原期限还差 30 s 以上）；原地到站后新停靠起点 = 到站那一轮，车上最新一版清单只列新需求、期限 = 到站 + 60 s |
| `AnAppendBeforeTheLoadCommitsLeavesTheGraceForACorrectionTheServerAuthorizes` | 审查 S1 的零窗口探针：追加早于落定，落定那一轮停在 `AwaitingStationDeparture`（从库里读到）、没发核验；宽限内走服务端真实授权路径发起纠错，不被拒、`LoadCorrectionCommand` 进发件箱；宽限过了纠错还开着，不走，阻断原因 `LOAD_CORRECTION_IN_PROGRESS` |
| `AnAppendBeforeTheLoadCommitsEndsTheWaitWhenTheGraceRunsOut` | 追加早于落定、没人纠错：落定 + 宽限 − 1 s 仍在等，落定 + 宽限那一轮发核验 |
| `ACorrectionClosedInTheGraceEarnsAnotherGraceBeforeTheVehicleIsSentOn` | 宽限内开的纠错闭环：起点 = 闭环时刻、阻断原因清空；闭环 + 宽限 − 1 s 仍在等，闭环 + 宽限发核验，早于闭环 + 完整时长 |
| `WithTheGraceAtZeroTheWaitEndsInTheRoundTheLoadCommits` | 宽限为零：退回第一版行为，落定那一轮就发核验 |
| `AnAppendAtAnotherStationStillWaitsOutTheStationDeadline` | 追加在另一站：期限前 2 s 仍不发核验，到期那一轮发 |
| `ASameStationAppendDoesNotEndTheWaitWhileTheCurrentStopHasALoadOutstanding` | 当前停靠还挂着一条待装（库里直接构造，正常流程走不到）：不提前 |
| `ASameStationAppendDoesNotEndTheWaitWhileACorrectionIsOpen` | 纠错开着：不提前，阻断原因是纠错进行中 |
| `ASameStationAppendDoesNotSendAVehicleHeldForItsSlotDoorOn` | 仓门扣车：原期限过了 10 s 也不发核验 |
| `ASameStationUnloadNextDoesNotEndThePickupWait` | 下一个是同站卸货（先装后卸）：照旧等满，归 control-server#647 |
| `ARemovedSameStationStopDoesNotEndTheWait` | 审查 L1 X2：当前停靠与关卡之间夹着一个已删的同站取货停靠（旧版本修订留下的序位，库里直接构造），不算同站追加 |
| `AClosedCorrectionUnderASlotDoorHoldLeavesNoStaleCorrectionReason` | 审查 L1 X4：纠错闭环时车又被仓门扣着，宽限过后不发核验、不残留 `LOAD_CORRECTION_IN_PROGRESS` |

不放行的几条都把钟拨到期限前（或宽限前）一点再跑：只跑一轮、钟不走时「没发核验」对修前修后都成立。核验按停靠认（该停靠的 `DepartureSafetyCheckMessageId` 是否进了发件箱），不按条数。
纠错闭环是直接把纠错行写成 `Reconciled`（与 `LoadedStopDeadlineReachesVehicleTests` 同一个做法），授权走的是真实路径。

## 变异验证（`mutation/`）

脚本从修后文件按内容改出每个变异，构建，构建失败的标 `BUILD FAILED` 不跑测试，否则跑本类 12 条，按字节写回。每个日志里都有构建那一行 `0 Error(s)`。跑完 `git diff -- src` 只剩本票的改动。

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| R0 | 整段提前结束删掉（等于修前） | 宽限已过同站追加、宽限到才走、闭环再给宽限、宽限为零 |
| R1 | 引擎整个换回第一版 18708d80（有提前结束、没有宽限） | **零窗口探针** AnAppendBeforeTheLoadCommitsLeavesTheGraceForACorrectionTheServerAuthorizes、宽限到才走、闭环再给宽限 |
| M1 | 去掉「同站」比较 | AnAppendAtAnotherStation… |
| M2 | 去掉「本停靠已无待装」 | …WhileTheCurrentStopHasALoadOutstanding |
| M3 | 纠错闸对「下一个是同站取货」放行（提前结束绕过纠错闸） | …WhileACorrectionIsOpen、零窗口探针、AClosedCorrectionUnderASlotDoorHold…、闭环再给宽限 |
| M4 | 去掉「下一个停靠是取货」 | ASameStationUnloadNext… |
| M5 | 去掉「当前停靠是取货」 | **无（等价变异）**：卸货停靠在前面那一支已先返回 	rue；剩下的角色只有 CHARGER，充电停靠由充电那几支（JourneyRuntimeEngine.Charging.cs 等）推进，读代码看不到它经装货落定进入离站等待的路，没有构造（推的，未实测）。保留这一项是让条件自己读得懂 |
| M6 | 删掉调用方的仓门扣车闸 | …HeldForItsSlotDoorOn、AClosedCorrectionUnderASlotDoorHold… |
| M7 | 去掉宽限这一项（在修后代码上） | 零窗口探针、宽限到才走、闭环再给宽限 |
| M8 | 宽限比较 >= 改成 > | 宽限为零、宽限到才走、闭环再给宽限 |
| X2 | OpenStops 换成 Stops（审查 L1） | ARemovedSameStationStopDoesNotEndTheWait |
| X4 | 删掉起点更新那一块里的清阻断原因（审查 L1） | AClosedCorrectionUnderASlotDoorHold…、闭环再给宽限 |

第一轮跑 R1 时脚本把旧文件读坏了（编码），构建 251 个错误，测试跑的是上一个变异留下的二进制，结果与 R0 一模一样——那一行作废，脚本加上构建失败即跳过之后整轮重跑，上表是重跑的结果。

## 定向回归

改离站阶段、途中追加或调用 `ZeroChangePin` 的测试类，加 `ArchitectureTests`（过滤条件见 `regression-filter.txt`）：`Passed: 1685, Failed: 0`，退出码 0（见 `regression.txt`）。全量交给 CI。

## 车载端（第 4 步，只读，`w2g/fp-v2-impl` 的 `27ecf24`）

新停靠的清单与录入请求带的是新停靠自己的作业会话号、同一个站号。车载端 `WireToGateSessionClient` 收到清单按修订号整版替换，不比站号；
`WireToGateBusinessService.IsStopOf` / `EndsStopOf` 按「会话号 + 站号」配对，新会话号的清单与新会话号的录入请求互相认得，旧停靠的录入请求
早已被装货命令撤销（`WithdrawEntryRequestAnsweredBy`）。离站核验车载端只作答、不进入任何锁界面的状态。扫码入口 `CanSubmitSublot` 另要求
会话 Ready 与车停稳——原地「到站」车不动，满足。结论：新停靠开放后车载屏能立即显示新需求、接受扫码，**不需要改车载端**（读代码得出，没在真装置上跑）。

纠错入口（审查 S1 读到的）：车载端只对「最近一次完成的装货」开纠错入口（`WireToGateBusinessService.RecoveryVectors.cs` 的 `CanRequestLoadCorrection`、
`RequestLoadCorrectionCoreAsync` 取 `LastCompletedLoadOperationContext`）。宽限期内最近一次完成的装货就是本站这一条，入口是开的；新停靠一装完，入口换成新需求，
本站已装的那几条在车载端就再也纠正不了。
