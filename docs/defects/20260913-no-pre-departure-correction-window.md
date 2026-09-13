# 缺陷：装载提交后立即发车，没有离站前修正窗口；车已派走仍授权修正

Status: fixed（服务端）；车载端一处关联问题另记，见文末
Owner repository: `8005-agv-control-server`（`src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs`、`src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs`、`src/ControlServer.Host/Runtime/JourneyRuntimeOptions.cs`、`src/ControlServer.Infrastructure/Persistence/ControlServerDbContext.cs` 与迁移 `20260913131725_StationDepartureWait`）
Found by: 2026-09-13 为 `FP-IS-02` 写 G3 真车载端场景 `g3-pickup-load-and-correction` 时的调试运行（证据未入库：调试运行，产品提交未绑定）
Product at discovery: `fp/b2-close@f9babb91`
Fixed in: 见提交记录（本单与修复同一提交）
Peers: 车载端 `w2g/b3-on-v2@c86bac5`、slots-simulator `fb5f7c5`

**红在产品，偏离的是需求基线与已接受的 ADR。两端各自的 G2 都绿。**

## 规格

- `REQ-0237`：普通放错只在离站前纠正；车辆离开起点前保留 LoadCorrection 调整机会，离站后将错就错完成输送。
- ADR-cross-0054：装货物理闭环后自动提交，**StopClosureCommit 前仍可纠错**；纠错期间停止离站倒计时，重新闭环后从完整时长重新计时；车辆开始移动后不再允许普通纠错。
- ADR-cross-0055 / FR-031：服务端掌握离站等待期限，项目默认 5 分钟；期满后才同步投影、做出发前安全检查并向 RIoT 请求移动。
- `CONTEXT.md` 的 LoadCorrection 词条：它是普通存放错误**唯一**的系统纠正窗口。

## 现象

真车载端 + 真 slots-simulator 上两仓装载完成，车停在取货点，UIA 在门关上两秒后点「修正装货」并确认：

| 时刻 | 事实 |
| --- | --- |
| 20:19:17 | 第 2 仓关门，车载端报 `OperationResult` COMPLETED |
| 20:19:18 | 服务端**同一轮**判提交、发 `PreDepartureSafetyCheck`、建出 `TO_GATE` 意图并下到 RIoT |
| 20:19:19 | 车载端发 `LoadCorrectionRequested`；服务端只看装载是 `Committed` 就授权，发 `LoadCorrectionCommand` |
| 20:19:19 | 车载端收到命令，读车辆安全投影：车已有待执行的去关卡单，判未停稳，以 `VEHICLE_NOT_READY` 拒绝，**不回任何结果** |
| 之后 | 修正工作流停在 `AwaitingResult`；旅程 `AwaitingGateArrival` |

v2 服务端里 ADR-cross-0055 实现映射列出的那一套（`SublotWaitTimeout`、`JourneyStopRow`、`ConcludeLoadingStopAsync`）**零命中**——映射核对的是 MVP 线的服务端，v2 线从未有过离站等待。

两处偏离：

1. **没有窗口。**装载提交到发车之间只有一个轮询内的几十毫秒，现场不可能在这段时间里发起修正。`CV-LOAD-CORRECTION` 在 v2 上实际不可达。
2. **离站后仍授权。**`AuthorizeLoadCorrectionAsync` 的判据只有「装载 Committed、仓位在集合内」，与 `REQ-0237` 的「离站后将错就错」相反，给一辆已被派走的车下开门命令。

## 为什么 G2 没抓到

- 本仓没有任何测试调用过 `AuthorizeLoadCorrectionAsync`；`RecoveryStateMachineG2Tests` 里的修正命令是测试直接入队的（`QueueLoadCorrectionCommandAsync`），不经授权面。
- 旅程测试只走「提交 → 出发前检查」，从没有人在两者之间尝试修正，所以窗口是否存在不被任何断言触及。
- 车载端 `ONBOARD_HMI_G2` 的假服务端对修正请求直接下命令，不模拟派车状态。

## 修复

- 旅程新增阶段 `AwaitingStationDeparture`，装载提交后进入，`JourneyRuntimes.StationDepartureWaitStartedAt` 记本轮等待起点（服务端时钟）。
- 期满且没有未收敛的修正，才发 `PreDepartureSafetyCheck`，此后流程不变。
- 有未收敛的 `LOAD_CORRECTION` 工作流时车不走，`BlockReasonCode = LOAD_CORRECTION_IN_PROGRESS`（阶段不转 `Blocked`）；修正收敛后以服务端记录的收敛时刻为起点重新计满。
- `JourneyRuntime:stationDepartureWaitTimeout`，默认 5 分钟；`00:00:00` 关闭等待（同时没有修正窗口），非零时至少 5 秒。
- `AuthorizeLoadCorrectionAsync` 增加判据：旅程必须仍在 `AwaitingStationDeparture`，否则 `LoadCorrectionRejected` / `ACTION_NOT_ALLOWED_IN_STATE`，不建工作流、不发命令。
- 关闭等待时不多存一次：提交那一轮直接进入发车分支，保存次数与修复前相同。

## 本次没有做的

| 项 | 原因 |
| --- | --- |
| 车载端显示离站截止时间（FR-031 AC-4） | `protocol-v1.0.0` 的 `CurrentStopWorklistSnapshot` 没有 `stationDepartureDeadlineAt` 字段；v1.0.0 已发布，加字段是协议变更 |
| 期满取消本站尚未开始的待装需求（AC-5） | v2 一趟旅程只有一条需求，提交之后本站不存在「尚未开始的待装需求」 |
| 等待期间断联使截止失效、恢复后重新计满（AC-9） | 断联时车本来就不会走（就绪门）；恢复后按原起点继续。未加，未测 |
| 修正授权与期满离站同时发生（AC-6） | 两者之间有毫秒级竞态：授权读到 `AwaitingStationDeparture` 的同时引擎转出。输掉时车载端会因车辆已有移动单而拒绝开门（故障安全），修正工作流停在 `AwaitingResult`。未加并发令牌 |
| 车载端拒绝已授权的修正命令时不回报 | 车载端 `HandleRecoveryVectorCommandAsync` 在 `VEHICLE_NOT_READY` 时只记日志，服务端工作流永远等结果。服务端修复后正常路径不再触发，但竞态与现场异常仍可能触发；需车载端改动，另议 |

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 `AfterTheLoadCommitsTheVehicleWaitsOutTheStationDepartureWindowBeforeDeparting`、`AnOpenLoadCorrectionHoldsTheVehicleAndTheWaitStartsOverWhenItCloses`、`LoadCorrectionIsAuthorizedOnlyWhileTheVehicleStillWaitsAtThePickup`（三例） | 修复前 4 红（授权例 1 绿），修复后全绿 |
| 新增 `StationDepartureWaitDefaultsToFiveMinutesAndIsEitherOffOrAtLeastFiveSeconds` | 绿 |
| 既有测试 | 两个夹具显式把等待设为 0，行为与修复前一致；`Batch3MigrationDisciplineTests` 的迁移名单加入本迁移 |
| 全量 | `715 passed / 0 skipped` |

真车载端上的修正全流程在 G3 `FP-IS-02` 的 `G3-02-07`～`G3-02-13` 里核对，那份证据出来后补进本表。
