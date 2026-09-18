# 缺陷：journey 场景 `g3-forced-mechanical-recovery` 不按 onboard-hmi#107 加的「已隔离并完成机械取出」，车载端一直不上报结果

Status: open（场景脚本缺陷，不是产品缺陷；修复票待调度会话开）
Owner repository: `8005-agv-control-server`（`scripts/l2/scenarios/g3-forced-mechanical-recovery.ps1`，第 52 行按下入口之后）
Found by: control-server#90（批次 5 出口）修复后的重跑，`run-journey-g3.ps1`，2026-09-19，
[`evidence/g3/20260919-protocol-v2.0.0-journey-c12f0498/`](../../evidence/g3/20260919-protocol-v2.0.0-journey-c12f0498/)
Product at discovery: control-server `c12f0498`（harness `69894550`）；onboard-hmi `29fbf65e`；slots-simulator `fb5f7c59`；协议 `protocol-v2.0.0@86575456`

## 现象

`JOURNEY_G3_SLICE_FAIL`。12 条场景里 11 条退出码 0，只有 `g3-forced-mechanical-recovery` 中止：

```
Timed out after 120s waiting for: the server received ForcedMechanicalRecoveryResult, or the onboard reported a refusal. Last observed: (nothing)
```

它走到了按下入口那一步：车载端重启、那一仓结算为 `UNKNOWN`、旅程 `Blocked`，`onboard-forced-recovery-entry = True`（缺陷单 A 修复后入口正常出现），
场景按下「强制机械恢复」并答了确认框。之后 120 秒服务端没收到 `ForcedMechanicalRecoveryResult`。

因为这条场景中止，运行级断言 `noScenarioAbortedBeforeItsJudgments` 失败；它计入每一片，所以 `FP-IS-01`、`02`、`03`、`07` 都判 `FAIL`，
尽管这三片自己的场景（以及 `FP-IS-07` 的其余四条）全部通过。

## 卡在哪一步

收尾快照：

| 表 | 状态 |
| --- | --- |
| `ExceptionRecoverySessions` | `EXECUTING`，`SelectedAction = FORCED_MECHANICAL_RECOVERY` |
| `RecoveryWorkflows` | `FORCED_MECHANICAL_RECOVERY`，`AwaitingResult`，`CommandMessageType = ForcedMechanicalRecoveryCommand`，`ResultMessageId` 为空 |

服务端已经下发命令，在等车载端的结果。车载端按 onboard-hmi#107（PR #110，`8f308bb1`）的设计，收到强制机械恢复命令后**不发开锁 DO**，
等操作员在 HMI 上按「已隔离并完成机械取出」（`CanConfirmForcedMechanicalRecovery`）才上报 `ForcedMechanicalRecoveryResult`
（车载端测试 `RecoveryVectorG2Tests.ForcedMechanicalRecoverySendsNoUnlockAndReportsOnlyAfterTheOperatorConfirms`）。
场景里没有这一步：第 45～54 行只等入口、按入口、然后直接等结果。

## 为什么现在才出现

- 场景最后一次修改在 control-server#137（`6c275166`、`f929b701`，合入 `3dcde12b`），当时 v2 车载端还没有 onboard-hmi#107，确认按钮不存在；
  #137 的 PR 写明「没跑真装置场景 `g3-forced-mechanical-recovery`」。
- control-server#128 合入前那次 PASS 用的是车载端 `8153946b`，同样早于 onboard-hmi#107。
- 批次 5 出口第一轮（`evidence/g3/20260918-protocol-v2.0.0-journey-06b65688/`）卡在更早的缺陷单 A（入口不出现），没走到这一步。

所以这是 onboard-hmi#107 合入以后，这条场景第一次在真车载端上走完入口。

## 修复要做什么（交调度会话开票）

- **所在仓**：`8005-agv-control-server`。
- **改哪里**：`g3-forced-mechanical-recovery.ps1` 在按下「强制机械恢复」并确认之后，等「已隔离并完成机械取出」可用（`Wait-G3ButtonOffered`），按下并答确认框（若有），
  再等服务端收到结果。场景里的「现场先把仓门撬开」若车载端或模拟器需要物理状态配合，一并写进场景。
- **核对 `G3-07-44`**：control-server#137 把它改成「结果之后车辆仍 `RecoveryRequired`，直到硬件恢复记录」。onboard-hmi#107 另加了管理员手动提交硬件恢复记录的入口；
  场景不要按那一个，否则 `G3-07-44` 会读到已解除的状态。
- **怎么证明红变绿**：在 `c12f0498`＋修复、车载端 `29fbf65e` 上单跑这条场景 PASS（`G3-07-41`～`45`）；修复前的红证据即本单的 `Found by`。
- **对 control-server#90 的影响**：journey runner 从 G3 共享绑定的 `ControlServerCommit` 克隆场景脚本，所以修复合入后要把绑定移到新提交；
  为了四份 G3 证据绑同一身份，建议四个 G3 runner 一起重跑（约 17 分钟）。车载端 G2、真装置七条、服务端 G2 与 CI 三连都不经过这个文件，证据保留。
