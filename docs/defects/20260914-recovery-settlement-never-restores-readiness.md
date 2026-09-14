# 缺陷：恢复收尾后会话不回到就绪，旅程停在已提交的装载上

Status: fixed（服务端）
Owner repository: `8005-agv-control-server`（`src/ControlServer.Host/Transport/OnboardMessageProcessor.cs`、`src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs`）
Found by: 2026-09-14 G3 `FP-IS-07` 真车载端场景 `g3-exception-resume` 调试运行 `resume-007`（断言 G3-07-10 红，证据在 `C:\g3dbg\resume-007`，未入库）；
补偿路径同一问题见 `C:\g3dbg\compensate-001\snapshots\db-SessionRecoveries.json`
Product at discovery: `fp/b2-close@d2a19c7a`；车载端 `w2g/b3-on-v2@b960108`
Fixed in: 见提交记录（本单与修复同一提交）

**红在产品。车载端所有动作都做对了；服务端知道操作已经结清，却没有据此重新判定会话。**

## 现象

`resume-007`：装载 `UNKNOWN` → 车载端重启，握手报告把这笔尝试列为未结清 → 申请恢复原操作 → 车载端重开、放货、报 `COMPLETED` →
服务端替换结果、装载 `Committed`、恢复工作流 `Reconciled`、恢复会话 `CLOSED`。之后：

| 表 | 值 |
| --- | --- |
| `JourneyRuntimes` | `Stage=AwaitingLoadResult`，`BlockReasonCode=ONBOARD_SESSION_NOT_READY` |
| `SessionRecoveries` | `Readiness=RecoveryRequired`，`ReasonCode=PENDING_FACT_RECONCILIATION_REQUIRED`，`PendingAttemptIdsJson=["3a5f2f0d-…"]`，`UnsettledSlotOperationAttemptId=3a5f2f0d-…` |

车货已装好，旅程要等车载端下一次重连、再报一份恢复状态报告才会继续。

`compensate-001`（补偿清空已对账、需求已取消）结束时会话同样停在 `RecoveryRequired / OPERATION_RECOVERY_REQUIRED`：下一张需求派不给这台车。

## 原因

- 会话的 `PendingAttemptIdsJson` / `UnsettledSlotOperationAttemptId` 只由 `RecoveryStateReport` 写入、由下一次握手清空。
  服务端之后结清了这笔尝试（恢复提交、补偿/交接/取消收敛），却从不把它从会话上划掉。
- 恢复向量的结果（`LoadCompensationResult` 等）根本不重新判定就绪；`OperationResult` 虽然会判，但只在转入 `RecoveryRequired` 时通知车载端。
  处理器那里原有一句注释写着：恢复完成后没有路径告诉车载端 `READY`，若车载端学不到，就在这里放宽。车载端确实学不到。

## 修复

- `WireToGateStore.SettleReportedAttemptsAsync`：把会话待结清尝试里、对应装载操作已 `Committed` 或 `Cancelled` 的尝试划掉，
  会话记的未结清尝试若在其中一并清空；操作仍是 `Prepared` / `RecoveryRequired` 的留着。
- `OnboardMessageProcessor`：
  - `OperationResult`：在划掉待结清结果之后调用它，再判就绪；通知条件由「只在转入 RecoveryRequired 时」放宽为「就绪确有变化时」，普通路径应答仍是一行。
  - 恢复向量结果（`FaultCargoRecoveryResult`、`ForcedMechanicalRecoveryResult`、`LoadCancellationResult`、`LoadCompensationResult`、`LoadCorrectionResult`）：
    协调器处理完后同样结清、判就绪，变化时在应答后附 `SessionReadiness`。

不会提前就绪：就绪判据（能力/安全快照、报告、发车安全、待结清事实、操作是否需恢复、强制恢复代数）一项未改，只是被按时重新判定。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 `RecoveryStateMachineG2Tests.ASuccessfulResumeSettlesTheReportedAttemptAndTellsTheVehicleItIsReady` | 修复前红（应答只有 1 行，`Expected: 2, Actual: 1`）；修复后绿：应答附 `SessionReadiness READY`，待结清尝试清空，会话 `Ready` |
| 新增 `RecoveryStateMachineG2Tests.ACompensationThatProvesEverySlotEmptyTellsTheVehicleItIsReadyAgain` | 修复前红（同上）；修复后绿：补偿对账、装载取消、会话 `Ready` 并通知 |
| 全量 | 722 passed |

| L2 `g3-exception-resume`（服务端 `e6b92ee3`，车载端 `b960108`；调试运行 `resume-008`，证据未入库） | 11/11 PASS：恢复收尾后旅程离开 `AwaitingLoadResult`，进入 `AwaitingStationDeparture`（修复前 `resume-007` 停在 `AwaitingLoadResult / ONBOARD_SESSION_NOT_READY`） |

真车载端上恢复之后旅程继续，由 G3 `FP-IS-07`（`g3-exception-resume`，断言 G3-07-10）在统一身份上正式核对。
