# 缺陷：staged G3 恢复探针在第一条强制机械取出未结清时提交第二条，control-server#187 之后被拒，runner 中止

Status: 出口分支内修复（调度 2026-09-19 定：测试代码，不另开票），修在 control-server#165 的 `b6-09/batch-6-exit`，重跑结果见文末
Owner repository: `8005-agv-control-server`（`scripts/run-staged-g3.ps1` 恢复探针，约 `:1076` 的 `ActionSubmit(... StableGuid("recovery:forced-two") ... "FORCED_MECHANICAL_RECOVERY" ...)` 与其后依赖它的判据）
Found by: control-server#165（批次 6 出口）封锁时段，`run-staged-g3.ps1`，2026-09-19 20:38～20:40（本地时间），
[`evidence/g3/20260919-protocol-v2.0.0-staged-905ffd1d/run-result.json`](../../evidence/g3/20260919-protocol-v2.0.0-staged-905ffd1d/run-result.json)；
同一封锁内重跑一次，同样中止：[`evidence/g3/20260919-protocol-v2.0.0-staged-905ffd1d-rerun/run-result.json`](../../evidence/g3/20260919-protocol-v2.0.0-staged-905ffd1d-rerun/run-result.json)
Product at discovery: control-server `905ffd1d`（runner `76c2ca21`，只多了 G3 绑定与出口报告）；onboard-hmi `44b3aa6e`；slots-simulator `fb5f7c59`；协议 `protocol-v2.0.0@86575456`

## 现象

`run-staged-g3.ps1` 两次都以 `INCONCLUSIVE_RUNNER_ERROR` 结束，五片（`FP-IS-00`、`06`、`07`、`14`、`15`）全部 `INCONCLUSIVE`：

```
Exception calling "GetResult" with "0" argument(s): "Expected a RecoveryActionAccepted response."
```

`recovery-probe-events.ndjson` 的最后一条是 `forced-mechanical-command-replayed-after-reconnect`（`PASS`，第 1 代命令在重连后按原 `recoveryActionId` 重发）。紧接着探针用新的 `recoveryActionId`
（`StableGuid("recovery:forced-two")`）在同一恢复会话里再提交一次 `FORCED_MECHANICAL_RECOVERY`，期待 `RecoveryActionAccepted`，没有等到，探针抛异常、runner 中止。
依赖这一步的七条断言因此记成 `FAIL_OR_INCONCLUSIVE`：

```
recoverySessionAuthorisationBoundary, recoveryActionsRefusedWithoutPersistedOperation, hardwareRecoveryRecordScopeEnforced,
recoveryCommandSurvivesMidFlightDisconnect, forcedRecoveryGenerationAdvancesMonotonically,
supersededGenerationResultIsHistoricalEvidenceOnly, recoveryNeverReportsFalseCompletion
```

其余断言（同连接重放、业务面故障注入、身份与配置等）都是 `PASS`。两次运行一致，是确定性复现，不是时序偶发。

同一轮的另外三个 G3 runner 都绿：restart `STAGED_G3_PROCESS_RESTART_PASS`、需求承载 `DEMAND_BEARING_G3_VECTORS_PASS`、journey `JOURNEY_G3_PASS`（14/14，含 `g3-forced-mechanical-recovery`）。

## 为什么是判据落后，不是产品缺陷

control-server#187（PR [#190](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/190)，合并提交 `c1252932`）**有意**改了这里的行为。PR 正文「做了什么」第 ① 条：
同一恢复会话里，`COMPENSATE_LOAD_ALL_EMPTY`、`FAULT_CARGO_HANDOFF`、`FORCED_MECHANICAL_RECOVERY` 已有同类型工作流处于 `CommandPending`／`AwaitingResult` 时，
再用新的 `recoveryActionId` 提交同一动作，回 `RecoveryActionRejected`／`ActionNotAllowedInState`，**强制取出不推进代次**。判断在
`src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs` 的 `SubmitActionAsync`，位于代次推进之前。

staged 探针的第二次提交正好落在这里：第 1 代强制取出的命令已重发、结果还没回，工作流在 `CommandPending`／`AwaitingResult`。按 #187 之后的设计，服务端回的是拒绝而不是受理。

PR #190 自己也写明了后果：12 例既有 L1 测试的第二次提交改经辅助方法 `ProcessAsBeforeCs187Async` 模拟「修复前写进库的存量数据」，
「迟到结果」与「旧代次只作历史证据」那条路在修复后只剩存量数据能走到，由 `ALateForcedRecoveryOfAClosedSessionIsHistoricalAndSettlesNothing` 等测试守着。
staged 探针没有随之改，#187 的票也没有跑 G3（批次 6 的 G3 集中在出口票），所以直到出口才撞上——与批次 5 出口的缺陷单 B
（[`20260919-staged-g3-forced-recovery-criteria-predate-cs137.md`](20260919-staged-g3-forced-recovery-criteria-predate-cs137.md)）是同一类。

协议不要求受理第二条并发的强制取出：向量 `CV-FORCED-MECHANICAL-RECOVERY` 对服务端只断言 `FENCE_FORCED_RECOVERY_BY_GENERATION`（旧代次的结果被围栏），
不规定同一会话里未结清时能否再提交。所以这不是契约冲突，不走协议仓。

## 修复方向（由修复票定）

1. 探针的第二次提交改为断言 #187 的新行为：`RecoveryActionRejected`、原因码 `ActionNotAllowedInState`、车的强制代次不推进、不多发命令。
2. 「代次推进」「旧代次只作历史证据」「不报假完成」三条判据要换一条在 #187 之后仍可达的路径取证，或者像 PR #190 的 L1 那样明确降为存量数据防御，并在 staged 头注释与
   `g3-slice-evidence.ps1` 的归属说明里写清 G3 这一面还证明什么、不再证明什么。
3. 按缺陷单 B 的先例，修复要带「新判据绿、旧判据红」的两次 staged 运行作证据。

## 对批次 6 出口的影响

- `FP-IS-10`、`FP-IS-11` 的 G3 面由 journey 证明，不受影响（`evidence/g3/20260919-protocol-v2.0.0-journey-905ffd1d/`）。
- staged 负责的 `FP-IS-00`、`06`、`07`、`14`、`15` 的 staged 面本轮没有结论。出口报告按「修好后从头重跑受影响的门禁、不拼接」处理：修复合入后移 G3 绑定、从头重跑 staged。

## 修复（出口分支内）

调度 2026-09-19 定走「改名」方案，不在旧名下改判内容：断言名就是它证明的东西。

| 判据 | 处理 |
| --- | --- |
| `forcedRecoveryGenerationAdvancesMonotonically` | 名字仍准确，保留。改判「受理一次推进一次、被拒的第二条不推进」：车的强制代次 = 1，唯一的强制工作流 `Reconciled`、代次 1，结果证据非历史、代次 1 |
| `supersededGenerationResultIsHistoricalEvidenceOnly` → **`secondForcedRecoveryWhileFirstUnsettledIsRejected`** | 改名。判「第一条未结清时第二条被拒」：`RecoveryActionRejected`／`ACTION_NOT_ALLOWED_IN_STATE`，第二条没有工作流、没有结果证据、没有命令（发件箱强制命令只有第一条那一行），会话代次 1。原「旧代次结果只作历史证据」在 staged 上已不可达，改由 PR #190 那 12 例 L1 承担，点名 `RecoveryStateMachineG2Tests.ALateForcedRecoveryOfAClosedSessionIsHistoricalAndSettlesNothing` |
| `recoveryNeverReportsFalseCompletion` | 名字仍准确，保留。「两个工作流、硬件记录挂第二条」改为「一个工作流、硬件记录挂第一条」 |
| `recoveryCommandSurvivesMidFlightDisconnect` | 发件箱强制命令行数由 2 改为 1（第二条被拒不发命令） |

探针（C# harness）的改动：第二次提交期待 `RecoveryActionRejected` 并记一条 `second-forced-recovery-while-first-unsettled-rejected`；第一条用它自己的、当前代次的结果结清；
重放与内容冲突、硬件记录与范围不符都改指第一条。`g3-slice-evidence.ps1` 的 `FP-IS-07` 名单同步换名。

**离线自检**：`scripts/Test-G3RunnerClaims.ps1` 通过（`G3_CLAIMS_OK`，staged 报告 29 个、`FP-IS-07` 7 个）；harness here-string 用 `Add-Type` 单独编译通过。

**红证据**：旧判据在 #187 之后的产品上两次中止（上文两份 `run-result.json`），即「新行为跑在旧判据上」的红；调度定不另造红。

**依据**：control-server#187（PR #190 做了什么第 ① 条，合并提交 `c1252932`）；协议向量 `CV-FORCED-MECHANICAL-RECOVERY` 对服务端只断言 `FENCE_FORCED_RECOVERY_BY_GENERATION`，不要求受理第二条并发的强制取出。

**影响范围**：改的是 `run-staged-g3.ps1`（被另三个 runner 读 param 块、记哈希；需求承载还编译它的 harness）与共用的 `g3-slice-evidence.ps1`，所以四个 G3 runner 在出口提交上从头各跑一遍（调度 2026-09-19 定）。
