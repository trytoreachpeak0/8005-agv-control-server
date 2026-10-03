# 缺陷：G3-13-13 读人工清桩的结果一行，而那一行在清桩结束时就被清空（场景判据的竞态）

Found by: 批次 9 出口 G3（control-server#412 第 4 步），[`evidence/g3/20261002-protocol-v2.0.0-journey-8467480d/scenarios/g3-manual-station-clearance/SUMMARY.md`](../../evidence/g3/20261002-protocol-v2.0.0-journey-8467480d/scenarios/g3-manual-station-clearance/SUMMARY.md)
Status: resolved——判据在出口分支上改写（`b0f070db`，调度 Coordinator 9 批准越过出口票的冲突边界），journey 从头重跑
Owner repository: `8005-agv-control-server`（`scripts/l2/scenarios/g3-manual-station-clearance.ps1` 的 G3-13-13）；不是产品缺陷
Product at discovery: control-server `8467480d`，onboard-hmi `4e40e196`，slots-simulator `fb5f7c59`，protocol `protocol-v2.0.0`（`86575456`）

## 现象

journey G3 19 个场景里 18 个 PASS，146 条断言 145 条过；唯一红的是 `g3-manual-station-clearance` 的 G3-13-13，
整轮 `JOURNEY_G3_SLICE_FAIL`，`FP-IS-13` 的 `formalSlicePass` 为 false，其余八片都是 PASS。

```
expected: L2-OPERATOR / null / STATION_EMPTY / 充电点1 -> CONFIRMED / null / true / CONFIRMED_STATION_RELEASED
actual:   L2-OPERATOR /  / STATION_EMPTY / 充电点1 -> CONFIRMED /  / True /
```

线路与服务端都对：请求字段齐全，服务端回 `CONFIRMED`、`stationReleased=true`、`problem` 为空；同场景 G3-13-11、12、14 都过
（211 以 `CHARGER_RELEASED_ON_MANUAL_CLEARANCE` 释放，旅程以 `CHARGING_UNABLE_TO_CHARGE_CLEARED` 收尾）。红的只是最后一项：
车载端界面结果一行 `StationClearanceStatus` 在 30 秒等待里一直是空串（`timeline.jsonl` 第 48 行）。
车载端日志自己记下了结果（22:08:32.96 `人工清桩确认结果：…outcome=Confirmed，stationReleased=True`）。

## 机理

**车载端的设计是清桩一结束就清空结果一行，而这里确认即结束。**服务端随 Result 以 `CHARGING_UNABLE_TO_CHARGE_CLEARED` 收尾旅程，
结束清桩的快照紧跟 Result；车载端处理这份快照时把 `_stationClearanceUnanswered` 与 `_stationClearanceOutcome` 一并置空
（onboard-hmi `27b58310` 的 `WireToGateBusinessService.StationClearance.cs:314-315`，`4e40e196` 同样）。结果一行因此只在
「Result 到」与「结束快照到」之间存在几毫秒，读它是与收尾快照赛跑，**自 onboard-hmi#221 起就是这样**。cs#406 那次 PASS
（服务端 `5a830447`、车载端 `27b58310`）是轮询赶上了那个窗口。

**最初的判断错了，记在这里：**起初以为是 onboard-hmi#222 审查第 4 条（PR #235 的 `2f61ecc`、`c9784ae`）引入。对照否定了它：

| 运行 | 车载端 | G3-13-13 | 证据 |
| --- | --- | --- | --- |
| 出口 journey | `4e40e196`（含 hmi#222） | FAIL，结果一行空 | `evidence/g3/20261002-protocol-v2.0.0-journey-8467480d/` |
| 对照（单场景） | `27b58310`（hmi#222 之前，即 `c5b2f9fa^1`） | FAIL，同一实际值 | `evidence/l2/20261002-b9exit-control-g3-manual-station-clearance-hmi27b58310/` |

两个版本的车载端日志都有「人工清桩确认结果 …Confirmed…」一行，说明两边的按键都拿到了结果；hmi#222 改的只是「结束快照先到时仍告诉操作员」。

## 操作员实际看到什么（实读 `4e40e196`）

- 成功时**没有弹窗**（`MainWindow.xaml.cs:382-398`，只在失败时 `ShowRecoveryFailure`）。
- 按键的应答经 `PublishOperatorResponse("STATION_CLEARANCE_CONFIRMED", StatusText(settled))` → `MainViewModel.ApplyWireToGateOperatorEvent`
  进屏上的操作记录列表 `LogListBox`（归为成功类），文字是「服务端已确认清桩（充电桩 X），站点已释放。车辆与站点状态以服务端下发的为准。」，
  列表自动滚到最新一条，**这一条持续留在屏上**（直到被 `MaxLogEntries` 挤出或操作员清空）。它没有 AutomationId，也没有 ItemStatus，
  只能按文字找到。
- 「确认清桩」入口随清桩结束收起。

所以现场操作员**看得到**一条持续的「服务端已确认清桩、站点已释放」，在操作记录里，不在入口下方的结果一行。

## 修复（判据，不是产品）

`b0f070db` 把 G3-13-13 改为断言「操作员被告知了」，不再读结果一行：

- (a) 线路与服务端应答，与原判据相同；
- (b) 屏上操作记录列表出现上面那一句（按 UIA Name 找）；
- (c) 车载端日志按本次 `confirmationRequestId` 记下 `outcome=Confirmed`、`stationReleased=True`；
- (d) 「确认清桩」入口收起。

验证（单场景，服务端 `b0f070db`，模拟器 `fb5f7c59`；变异都是一次性 worktree 里的本地提交，未推送，跑完已删）：

| 运行 | 车载端 | 运行前写下的预期 | 结果 | 证据 `evidence/l2/` |
| --- | --- | --- | --- | --- |
| 新判据 | `4e40e196` | 全绿 | PASS（record shown、Confirmed/True、entry gone） | `20261002-b9exit-g3-13-13-new-criterion-hmi4e40e196/` |
| M1 去掉结果日志一行 | `f5da86a5`（本地） | 只红 (c) | **变异无效**：构建失败 `CA1822`（方法不再访问实例数据），没有进到判据 | `20261002-b9exit-g3-13-13-mutation-m1/` |
| M1b 日志写 `outcome=Unknown` | `7c338f2f`（本地） | 只红 (c) | 只红 (c)：`outcome=Unknown stationReleased=True` | `20261002-b9exit-g3-13-13-mutation-m1b/` |
| M2 改掉操作记录那一句的文字 | `229fd60b`（本地） | 只红 (b) | 只红 (b)：`record absent` | `20261002-b9exit-g3-13-13-mutation-m2/` |
| M3 清桩结束后入口仍显示 | `2aae5396`（本地） | 只红 (d) | 只红 (d)：`entry still offered` | `20261002-b9exit-g3-13-13-mutation-m3/` |

三个变异里 G3-13-11、12、14 都照常 PASS。

## 待决（交调度）

- 给操作记录里这一条（或给清桩结果）一个稳定的 AutomationId／ItemStatus，判据就不必按文字找，与 onboard-hmi#241 同类。
- 结果一行在清桩结束时清空，所以入口下方不留结论；要不要像 onboard-hmi#242 那样让它短暂保留，是产品取舍，批次 9 之后定。

## 证据精简

红轮 journey 目录入库前去掉了 18 个 PASS 场景的 `logs/control-server.out.log`（62M → 18M），红的那个场景原样保留；
对照与变异目录去掉了服务端日志、构建日志与库快照，保留 `assertions.json`、`SUMMARY.md`、`timeline.jsonl` 与车载端日志。
