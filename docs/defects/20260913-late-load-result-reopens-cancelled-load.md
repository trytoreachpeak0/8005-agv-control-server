# 缺陷：装载中取消收敛后，原装载迟到的 UNKNOWN 结果把需求与装载改回 RecoveryRequired

Status: fixed
Owner repository: `8005-agv-control-server`（`src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs` 的 `ApplyOperationResultAsync`）
Found by: 2026-09-13 G3 `FP-IS-02` 真车载端场景 `g3-load-cancellation` 的调试运行 `cancel-002`，判据 `G3-02-27` 红（证据未入库：调试运行）
Product at discovery: `fp/b2-close@21b8d041`
Fixed in: 见提交记录（本单与修复同一提交）
Peers: 车载端 `w2g/b3-on-v2@c86bac5`、slots-simulator `fb5f7c5`

**红在产品。取消向量本身的六条判据（`G3-02-21`～`G3-02-26`）全绿，坏的是取消收敛之后。**

## 现象

真车载端上两仓装载进行中，第 1 仓开着时操作员决定不装，把空仓门带上，点「取消装货」：

| 时刻 | 事实 |
| --- | --- |
| 装载开始 +2s | 车载端 `LoadCancellationStartRequested`；服务端 `AUTHORIZED`，范围 1、2 仓 |
| 同上 | 车载端证两仓全空、锁上、输出复位，报 `LoadCancellationResult` `ALL_EMPTY`；服务端收敛：工作流 `Reconciled`、需求 `Cancelled`、装载 `Cancelled`、租约释放、旅程 `Completed / CANCELLED_BY_OPERATOR` |
| 装载开始 +120s | 原装载的执行器等满车载端操作超时，报 `OperationResult` `UNKNOWN` |
| 之后 | 服务端把装载判为不安全结果：**装载 `RecoveryRequired`、需求 `RecoveryRequired`**；会话随之 `RECOVERY_REQUIRED / OPERATION_RECOVERY_REQUIRED` |

车被一次已经证过全空、已经终结的装载扣住，不能再接单。

## 原因

`ApplyOperationResultAsync` 不看操作当前的状态：只要结果不是「全部完成且物理事实安全」，就把操作改成
`RecoveryRequired`，把需求（只要不是 `Succeeded`）改成 `RecoveryRequired`。取消、补偿、故障交接收敛时把操作
写成 `Cancelled`，但没有任何一处阻止之后到达的原操作结果再改它。

车载端这一侧的行为是既有设计：取消执行器与装载执行器各有各的门，装载执行器不因取消而中止，超时后照常上报。
结果是 durable 的，服务端不能拒收，只能决定它还改不改事实。

## 为什么 G2 没抓到

`RecoveryStateMachineG2Tests.AuthorizedLoadCancellationReconcilesOnlyWhenEverySlotIsProvenEmpty` 证到取消收敛为止；
没有测试在收敛之后再送一份原装载的结果。车载端 G2 的假服务端也不会让两个执行器同时跑。

## 修复

`ApplyOperationResultAsync` 在核对完结果与操作的身份之后：操作已经是 `Cancelled` 时，结果照样落库（`HistoricalOnly = true`）、
照样确认，返回 `HistoricalOnly`，不再改操作、需求与旅程。`OnboardRecoveryCoordinator.ObserveOperationResultAsync`
对 `HistoricalOnly` 本来就不动工作流，会话就绪按 `StationOperations` 判，所以不再转 `RECOVERY_REQUIRED`。

## 证据

| 项 | 结果 |
| --- | --- |
| G3 调试运行 `cancel-002`（修复前，`21b8d041`） | `G3-02-27` 实际：需求 `RecoveryRequired` / 装载 `RecoveryRequired` / 迟到结果 `UNKNOWN→DurableAck` |
| 新增单元测试 `WireToGateStoreTests.ALateResultForALoadAlreadyCancelledIsKeptButReopensNothing` | 与修复同一次改动写成，没有单独在修复前的代码上跑红；修复前的红证据是上一行的 L2 运行 |
| `WireToGateStoreTests`、`RecoveryStateMachineG2Tests`、`OnboardMessageProcessorTests` | 38 passed |

真车载端上的终态在 G3 `FP-IS-02` 的 `G3-02-27` 里核对，那份证据出来后补进本表。
