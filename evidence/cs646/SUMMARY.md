# cs#646 证据：同站追加时提前结束原停靠的离站等待

本机 Windows，SDK 8.0.425，Debug，基于 `fp/v2-impl` 的 `fc2af82b4`（已含 cs#637 PR #642、cs#643 PR #651）。每次运行都看 `dotnet test` 的退出码，不只看汇总行。

## 改了什么

`JourneyRuntimeEngine.StationDepartureWaitIsOverAsync` 在纠错闸、卸货停靠那一支之后，计时之前，加一个条件：
当前停靠是取货、本停靠已无待装、紧接着的下一个开着的停靠是**同一站的取货停靠**时，返回「等待已结束」，不再读原倒计时。
不碰起点（离站时调用方清空，新停靠到站时种下自己的）。纠错闸在它前面照判；持货等单与仓门扣车两道闸在调用方、它后面照判；
离站核验与原地「到站」流程一字未改。

下一个停靠是同站**卸货**（规划器落下的先装后卸）不在条件里，照旧等满——那个顺序归 control-server#647。

## 用例（`tests/ControlServer.Tests/SameStationAppendEndsDepartureWaitTests.cs`）

| 用例 | 钉的是 |
| --- | --- |
| `ASameStationAppendSendsTheVehicleOnAtOnceAndTheNewStopGetsTheFullWait` | 同站追加后下一轮即发原停靠的离站核验（原期限还差 30 s 以上）；原地到站后新停靠起点 = 到站那一轮，车上最新一版清单只列新需求、期限 = 到站 + 60 s |
| `AnAppendAtAnotherStationStillWaitsOutTheStationDeadline` | 追加在另一站：期限前 2 s 仍不发核验，到期那一轮发 |
| `ASameStationAppendDoesNotEndTheWaitWhileTheCurrentStopHasALoadOutstanding` | 同站追加但当前停靠还挂着一条待装（库里直接构造，正常流程走不到）：不提前 |
| `ASameStationAppendDoesNotEndTheWaitWhileACorrectionIsOpen` | 同站追加但纠错开着：不提前，阻断原因 `LOAD_CORRECTION_IN_PROGRESS` |
| `ASameStationAppendDoesNotSendAVehicleHeldForItsSlotDoorOn` | 同站追加但仓门扣车：原期限过了 10 s 也不发核验 |
| `ASameStationUnloadNextDoesNotEndThePickupWait` | 下一个停靠是同站卸货：照旧等满（本票不越界到 cs#647） |

不放行的几条都把钟拨到期限前 2 s 再跑：只跑一轮、钟不走时「没发核验」对修前修后都成立。核验按停靠认（该停靠的 `DepartureSafetyCheckMessageId` 是否进了发件箱），不按条数。

## 变异验证（`mutation/`）

脚本从修后文件按内容改出每个变异，构建、跑本类 6 条，按字节写回；跑完 `git diff -- src` 只剩本票那一处。

| 变异 | 改了什么 | 红的用例 |
| --- | --- | --- |
| R0 | 整段删掉（等于修前） | `ASameStationAppendSendsTheVehicleOnAtOnce…` |
| M1 | 去掉「同站」比较 | `AnAppendAtAnotherStation…`、`ASameStationUnloadNext…`（后者因为 P1 的下一个是别站取货，也被提前） |
| M2 | 去掉「本停靠已无待装」 | `…WhileTheCurrentStopHasALoadOutstanding` |
| M3 | 整段挪到纠错闸之前 | `…WhileACorrectionIsOpen` |
| M4 | 去掉「下一个停靠是取货」 | `ASameStationUnloadNext…` |
| M5 | 去掉「当前停靠是取货」 | **无（等价变异）**：卸货停靠在前面那一支已经先返回 `true`；剩下的角色只有 `CHARGER`，充电停靠由充电那几支（`JourneyRuntimeEngine.Charging.cs` 等）推进，读代码看不到它经装货落定进入离站等待的路，没有构造（推的，未实测）。保留这一项是让条件自己读得懂，不靠上一支的位置 |
| M6 | 删掉调用方的仓门扣车闸 | `…HeldForItsSlotDoorOn` |

## 定向回归

改离站阶段、途中追加或调用 `ZeroChangePin` 的 63 个测试类，加 `ArchitectureTests`（过滤条件见 `regression-filter.txt`）：
`Passed: 1679, Failed: 0`，退出码 0（读到的，见 PR 正文）。全量交给 CI。

## 车载端（第 4 步，只读，`w2g/fp-v2-impl` 的 `27ecf24`）

新停靠的清单与录入请求带的是新停靠自己的作业会话号、同一个站号。车载端 `WireToGateSessionClient` 收到清单按修订号整版替换，不比站号；
`WireToGateBusinessService.IsStopOf` / `EndsStopOf` 按「会话号 + 站号」配对，新会话号的清单与新会话号的录入请求互相认得，旧停靠的录入请求
早已被装货命令撤销（`WithdrawEntryRequestAnsweredBy`）。离站核验车载端只作答、不进入任何锁界面的状态。扫码入口 `CanSubmitSublot` 另要求
会话 Ready 与车停稳——原地「到站」车不动，满足。结论：新停靠开放后车载屏能立即显示新需求、接受扫码，**不需要改车载端**（读代码得出，没在真装置上跑）。
