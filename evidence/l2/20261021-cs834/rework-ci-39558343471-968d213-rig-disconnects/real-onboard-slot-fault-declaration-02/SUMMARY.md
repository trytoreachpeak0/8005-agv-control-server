# L2 场景证据：real-onboard-slot-fault-declaration

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T070720020Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `a30ddd724e958ac7bd1ee90830812d48d3aac798` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `968d213707786dea63ccbdf12421aad42e9857e4` |
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
| stageRoot | `C:\actions-runner\vmwin01-control-server-desktop\_work\_temp\real-rig-39558343471-1\_stage\l2-20261021T070720020Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 判定被受理并已下发到车：202、PENDING、sentToVehicle，demandId 与尝试是这次装货的 | PASS | `202 / PENDING / True / ee6fe94b-90ca-441d-9156-34d4ea2c877e / f167c3ec-2ead-dc5c-a339-862c92469c93` | `202 / PENDING / True / ee6fe94b-90ca-441d-9156-34d4ea2c877e / f167c3ec-2ead-dc5c-a339-862c92469c93` |
| 线上 SlotFaultDeclarationResult：APPLIED、同一个尝试、problem 为空，服务端 DurableAck | PASS | `APPLIED / f167c3ec-2ead-dc5c-a339-862c92469c93 / problem null / DurableAck` | `APPLIED / f167c3ec-2ead-dc5c-a339-862c92469c93 / problem null / DurableAck` |
| OperationResult：overallOutcome UNKNOWN；1 号仓 UNKNOWN 且 reasonCodes 含 SLOT_FAULT_DECLARED；其余目标仓 NOT_STARTED | PASS | `UNKNOWN / slot 1 UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED` | `UNKNOWN / {"demandId":"ee6fe94b-90ca-441d-9156-34d4ea2c877e","slotOperationAttemptId":"f167c3ec-2ead-dc5c-a339-862c92469c93","operationType":"LOAD","overallOutcome":"UNKNOWN","slotResults":[{"slotNo":1,"outcome":"UNKNOWN","finalPhysicalState":"EMPTY","lockState":"UNLOCKED","unlockOutputState":"RESET","reasonCodes":["SLOT_FAULT_DECLARED"]}],"observedAt":"2026-10-21T15:08:09.7482709+08:00","journalCheckpoint":"ACTIVE_UNLOCK_SET","resultContentSha256":"08ccaa1f3fca615fa408a251f11ebe5d41ea710e1e7cdb1ca1aaa5260a7e7e67"}` |
| 旅程转 Blocked，原因 LOAD_RESULT_REQUIRES_RECOVERY | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 装货仓位操作 RecoveryRequired（既有 UNKNOWN 结算） | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 判定记录 APPLIED，审计带判定时车载端最近上报的读数与观测时刻 | PASS | `APPLIED / slot 1 / f167c3ec-2ead-dc5c-a339-862c92469c93 / readings with observedAt` | `APPLIED / slot 1 / f167c3ec-2ead-dc5c-a339-862c92469c93 / {"observedAt":"2026-10-21T07:07:35.7022293+00:00","safetyStateVersion":1,"lockState":"LOCKED","physicalState":"EMPTY","unlockOutputState":"RESET","changedSinceObserved":true}` |
| 判定之后零开锁：空关后屏上不给「重新开门」，或给了、按下被拒或这一仓不开锁；门一直关着、锁扣上，这次装货的 UNLOCKING 进度条数与判定前相同 | PASS | `不给重新开门，或按下不开 / 门一直 CLOSED/*/1/0 / UNLOCKING 1` | `空关后 10 秒屏上没有「重新开门」 / 空关后 +0s CLOSED/EMPTY/1/0 / CLOSED/EMPTY/1/0 / UNLOCKING 1（窗口内最多 1）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
