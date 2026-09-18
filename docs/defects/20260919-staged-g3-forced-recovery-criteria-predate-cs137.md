# 缺陷：staged G3 的三条强制机械取出判据仍断言 control-server#137 之前的「断头」行为

Status: fixed，由 [control-server#151](https://github.com/trytoreachpeak0/8005-agv-control-server/issues/151) 修复（PR [#153](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/153)，合并提交 `c3c81eaf`），control-server#90 重跑 PASS
Owner repository: `8005-agv-control-server`（`scripts/run-staged-g3.ps1` 第 3282～3308 行附近的 `$recoveryGenerationAdvancePass`、`$recoverySupersededResultHistoricalPass`、`$recoveryNoFalseClosurePass`）
Found by: control-server#90（批次 5 出口）第 4 步，`run-staged-g3.ps1`，2026-09-18 23:45:42～23:47:45（本地时间），
[`evidence/g3/20260918-protocol-v2.0.0-staged-06b65688/run-result.json`](../../evidence/g3/20260918-protocol-v2.0.0-staged-06b65688/run-result.json)
Product at discovery: control-server `e0f26b37`（runner `06b65688`，只多了 G3 绑定）；onboard-hmi `9748c418`；slots-simulator `fb5f7c59`；协议 `protocol-v2.0.0@86575456`

## 现象

`STAGED_SLICE_FAIL`：`FP-IS-00`、`FP-IS-06`、`FP-IS-14`、`FP-IS-15` 都是 `PASS`，只有 `FP-IS-07` 是 `FAIL`，红的是这三条：

```
forcedRecoveryGenerationAdvancesMonotonically     FAIL_OR_INCONCLUSIVE
supersededGenerationResultIsHistoricalEvidenceOnly FAIL_OR_INCONCLUSIVE
recoveryNeverReportsFalseCompletion               FAIL_OR_INCONCLUSIVE
```

`FP-IS-07` 的其余七条（恢复会话授权边界、无持久化操作时拒绝动作、硬件恢复记录范围、断线后恢复命令仍在、身份拒绝、无移动与外部副作用、密钥扫描）都是 `PASS`。

## 实际观测到的是什么

`run-result.json` 的 `database` 与 `recoveryProbe`：

| 观测 | 值 |
| --- | --- |
| 探针自己的代次分支检查 `forcedRecoveryGenerationBranches.status` | `PASS`：两代各有一个动作，过期代次的结果被确认、重放逐字节一致、内容冲突时关连接 |
| `vehicleForcedRecoveryGeneration` | 2 |
| 第 1 代强制工作流 | `HistoricalOnly`，代次 1 |
| 第 2 代强制工作流 | **`Reconciled`**，代次 2，`demandId` 与 `slotOperationAttemptId` 都为空 |
| 恢复会话 | **`CLOSED`**，`selectedAction = FORCED_MECHANICAL_RECOVERY`，代次 2，`demandId` 为空 |
| `closedExceptionRecoverySessionCount`／`reconciledRecoveryWorkflowCount` | 1／1 |
| 建单、需求、站点作业 | 没有 |

判据要的是第 2 代工作流停在 `RecoveryRequired`、恢复会话停在 `EXECUTING`、`closedExceptionRecoverySessionCount = 0`、`reconciledRecoveryWorkflowCount = 0`。
三条判据都把这几项写进了合取式，所以同一个事实让三条一起红；其中「代次推进」与「旧代次只作历史证据」本身都已成立。

## 为什么是判据落后，不是产品缺陷

control-server#137（PR #140，合并提交 `3dcde12b`）**有意**改了这里的行为。PR 正文的对照表第 3 行：

> 恢复会话能结束，同车能开新会话 —— 改前：会话永远 `EXECUTING`，开新会话被拒；改后：会话 `CLOSED`，同车可以开新会话

同一 PR 写明工作流置 `Reconciled`、会话转 `CLOSED` 并推快照；车辆在硬件恢复记录到达之前保持不就绪（`RecoveryStateMachineG2Tests.AfterAForcedRecoveryTheVehicleStaysUnreadyUntilAHardwareRecoveryRecordForItArrives`）。
它同步改了真装置 journey 场景 `g3-forced-mechanical-recovery` 的 `G3-07-44`，但没有改 staged runner 里这三条。staged runner 的注释

> A forced mechanical recovery is an isolation, not a completion: nothing in this plane may close the session, reconcile a workflow ...

描述的正是 control-server#137 移除的那条断头路。

「强制取出是隔离、不是完成」这层意思在新行为下由车辆就绪承担，而不是由会话状态承担：会话关了，车仍然 `RecoveryRequired`，直到管理员提交硬件恢复记录。

## 修复要做什么（交调度会话开票）

- **所在仓**：`8005-agv-control-server`。
- **改哪里**：`scripts/run-staged-g3.ps1` 的三条判据按 control-server#137 之后的行为改写：
  - 第 2 代强制工作流 `Reconciled`、会话 `CLOSED`（带代次 2）是期望值；
  - 第 1 代仍 `HistoricalOnly`；
  - 「没有虚假完成」改为断言：没有建单、没有需求、没有站点作业，工作流不带 `demandId`／`slotOperationAttemptId`，**车辆就绪仍不成立**（`RecoveryRequired`），直到硬件恢复记录到达。
  - 若改名，同步 `g3-slice-evidence.ps1` 的归属表，并保留 `FP-IS-07` 归属。
- **怎么证明红变绿**：在同一绑定（`e0f26b37`／`9748c418`／`fb5f7c59`／`86575456`）上重跑 `run-staged-g3.ps1`，`FP-IS-07` 由 `FAIL` 变 `PASS`；
  另外把判据退回旧版本、在同一绑定上跑一次，必须仍红（证明新判据不是把断言删弱到恒真）。
- **影响的出口证据**：只影响 staged runner。按「修好后从头重跑受影响的门禁、不拼接」的规矩，control-server#90 在修复合入后重跑整个 `run-staged-g3.ps1`。
  G2 两端与 CI 三连不碰这个脚本，证据保留。

## 修复

control-server#151（PR #153）按 cs#137 之后的行为改写了三条判据：第 2 代强制工作流 `Reconciled`、会话 `CLOSED`（代次 2）为期望值；第 1 代仍 `HistoricalOnly`，且没有硬件记录指向它；「没有虚假完成」改为断言没有建单、需求、站点作业，工作流与会话不带需求或站点作业，唯一的硬件恢复记录指向第 2 代工作流及其会话，恢复车辆就绪为 `RecoveryRequired`。判据名与 `FP-IS-07` 归属不变。

同一绑定上：新判据 `STAGED_G3_RECOVERY_REPLAY_PASS`（`evidence/g3/20260919-b5-151-staged-new-criteria-06b65688`）；判据退回旧版本仍 `STAGED_SLICE_FAIL`、红的正是这三条（`evidence/g3/20260919-b5-151-staged-old-criteria-06b65688`）。

覆盖边界：staged 探针的恢复车辆不完成握手（就绪原因 `HANDSHAKE_INCOMPLETE`），看不到强制取出单独挡住就绪；直接证明在 L1 `RecoveryStateMachineG2Tests.AfterAForcedRecoveryTheVehicleStaysUnreadyUntilAHardwareRecoveryRecordForItArrives` 与真装置场景 `g3-forced-mechanical-recovery` 的 `G3-07-44`。

证据：
- 新判据 PASS：`evidence/g3/20260919-b5-151-staged-new-criteria-06b65688`（harness `166acd80`）
- 旧判据仍 FAIL：`evidence/g3/20260919-b5-151-staged-old-criteria-06b65688`（harness `93b09ebb`）

**control-server#90 的复验**（2026-09-19，修复合入后在出口身份上从头重跑）：`run-staged-g3.ps1` 在 control-server `c12f0498`（harness `69894550`）、
onboard-hmi `29fbf65e`、slots-simulator `fb5f7c59`、`protocol-v2.0.0@86575456` 上 `STAGED_G3_RECOVERY_REPLAY_PASS`，
`FP-IS-00`、`06`、`07`、`14`、`15` 全部 `PASS`：[`evidence/g3/20260919-protocol-v2.0.0-staged-c12f0498/`](../../evidence/g3/20260919-protocol-v2.0.0-staged-c12f0498/)。
上面覆盖边界里提到的 `G3-07-44` 那一半，本轮 journey 里的 `g3-forced-mechanical-recovery` 没走到判据（另一处场景缺陷，见
[`20260919-g3-forced-recovery-scenario-skips-isolation-confirm.md`](20260919-g3-forced-recovery-scenario-skips-isolation-confirm.md)），L1 那一半在全量测试里通过。
## 为什么没有早点发现

control-server#137 的 PR 写明「没跑真装置场景 `g3-forced-mechanical-recovery`」，staged runner 也没有跑：它们都要本机真装置时段，而当时时段被 control-server#88 占用。
按批次 5 的安排，这些复跑都并进了 control-server#90，所以第一次在新行为上跑 staged runner 就是出口这一轮。
