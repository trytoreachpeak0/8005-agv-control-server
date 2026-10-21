# L2 场景证据：real-onboard-slot-fault-declaration

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T062000431Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `24e7f55dd26c916b882de5250a731c5cf21c544e` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `27c5fa2f2075983f4d9577036d411bdd8b35fc89` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\vmwin01-control-server-desktop\_work\_temp\real-rig-39553412432-1\_stage\l2-20261021T062000431Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 判定被受理并已下发到车：202、PENDING、sentToVehicle，demandId 与尝试是这次装货的 | PASS | `202 / PENDING / True / 14107661-6895-4dbf-a194-8747fb6cb2ef / 414a86df-3cae-245a-86e2-faf9d345b89a` | `202 / PENDING / True / 14107661-6895-4dbf-a194-8747fb6cb2ef / 414a86df-3cae-245a-86e2-faf9d345b89a` |
| 线上 SlotFaultDeclarationResult：APPLIED、同一个尝试、problem 为空，服务端 DurableAck | PASS | `APPLIED / 414a86df-3cae-245a-86e2-faf9d345b89a / problem null / DurableAck` | `APPLIED / 414a86df-3cae-245a-86e2-faf9d345b89a / problem null / DurableAck` |
| OperationResult：overallOutcome UNKNOWN；1 号仓 UNKNOWN 且 reasonCodes 含 SLOT_FAULT_DECLARED；其余目标仓 NOT_STARTED | FAIL | `UNKNOWN / slot 1 UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED` | `(no OperationResult)` |
| 旅程转 Blocked，原因 LOAD_RESULT_REQUIRES_RECOVERY | FAIL | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `AwaitingLoadResult /` |
| 装货仓位操作 RecoveryRequired（既有 UNKNOWN 结算） | FAIL | `RecoveryRequired` | `Prepared` |
| 判定记录 APPLIED，审计带判定时车载端最近上报的读数与观测时刻 | PASS | `APPLIED / slot 1 / 414a86df-3cae-245a-86e2-faf9d345b89a / readings with observedAt` | `APPLIED / slot 1 / 414a86df-3cae-245a-86e2-faf9d345b89a / {"observedAt":"2026-10-21T06:20:33.2300891+00:00","safetyStateVersion":6,"lockState":"UNLOCKED","physicalState":"EMPTY","unlockOutputState":"RESET","changedSinceObserved":false}` |
| 判定之后零开锁：空关后屏上不给「重新开门」，或给了、按下被拒或这一仓不开锁；门一直关着、锁扣上，这次装货的 UNLOCKING 进度条数与判定前相同 | FAIL | `不给重新开门，或按下不开 / 门一直 CLOSED/*/1/0 / UNLOCKING 1` | `空关后 0.7 秒屏上给了「重新开门」，按下：没有弹出「无法重新开门」 / 空关后 +0s CLOSED/EMPTY/1/0；按键后 +0s CLOSED/EMPTY/1/0 / CLOSED/EMPTY/1/0 / UNLOCKING 2（窗口内最多 2）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
