# L2 场景证据：g3-load-cancellation-before-load

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261018T042252011Z` |
| agvId | `AGV-L2-001` |
| batchId | `v5-exit` |
| controlServerCommit | `63d83a0f623a99d0260c2b1c95672a91584336b5` |
| onboardHmiCommit | `a3a6753f6924b8f845483d6b886259c90af9b018` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261018T042252011Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到站、录入请求挂着、还没录入任何子批时，车载端清单里这一行出现可用的「取消」（机台取货停靠每行一个，CP-0016；不依赖恢复开关） | PASS | `row 取消 pressable=True` | `row 取消 pressable=True / bottom 取消装货 offered=False` |
| 取消经服务端授权且不带仓位操作：请求不带 slotOperationAttemptId，应答是 LoadCancellationAuthorization AUTHORIZED，指向这条需求、这次取消，slots 为空（AUTHORIZE_CANCELLATION_WITHOUT_SLOT_OPERATION） | PASS | `LoadCancellationAuthorization AUTHORIZED slots=[] attempt=` | `LoadCancellationAuthorization AUTHORIZED slots=[] attempt=` |
| 消息顺序与向量一致：LoadCancellationStartRequested → LoadCancellationAuthorization → LoadCancellationResult → DurableAck，各一次；车载端没有在授权之前单方面报结果（CV-LOAD-CANCELLATION-BEFORE-LOAD orderedExpectedMessages / NEVER_CANCEL_UNILATERALLY） | PASS | `StartRequested → Authorization < Result → DurableAck，各 1` | `StartRequested×1→LoadCancellationAuthorization / Result×1→DurableAck / 有序=True` |
| 车载端报 ALL_EMPTY 且不碰仓位 IO：结果不带逐仓条目，车载端没有报过任何 OperationProgress，模拟器上每个仓的门、货、锁反馈、开锁输出与到站前逐仓相同（REPORT_ALL_EMPTY_WITHOUT_SLOT_IO；forbidden: duplicate-slot-unlock、expanded-active-unlock-set） | PASS | `ALL_EMPTY / 0 条逐仓结果 / 0 条进度 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0 3=CLOSED/EMPTY/1/0 4=CLOSED/EMPTY/1/0 5=CLOSED/EMPTY/1/0 6=CLOSED/EMPTY/1/0 7=CLOSED/EMPTY/1/0 8=CLOSED/EMPTY/1/0` | `ALL_EMPTY / 0 条逐仓结果 / 0 条进度 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0 3=CLOSED/EMPTY/1/0 4=CLOSED/EMPTY/1/0 5=CLOSED/EMPTY/1/0 6=CLOSED/EMPTY/1/0 7=CLOSED/EMPTY/1/0 8=CLOSED/EMPTY/1/0` |
| 只在收到 ALL_EMPTY 结果之后终结：取消工作流无 attempt、仓集合为空、收下的正是车报的那条结果并已收敛；需求 Cancelled、旅程以 CANCELLED_BY_OPERATOR 收尾，车辆租约的释放时刻不早于服务端收到结果（TERMINATE_ONLY_ON_ALL_EMPTY_RESULT） | PASS | `LOAD_CANCELLATION / attempt= / [] / 收下 75d7b091-89dc-6f56-9bfe-2a1260e383a0 / Reconciled；Cancelled / Completed/CANCELLED_BY_OPERATOR / 释放 >= 2026-10-18T04:23:34.4584900+00:00` | `LOAD_CANCELLATION / attempt= / [] / 收下 75d7b091-89dc-6f56-9bfe-2a1260e383a0 / Reconciled；Cancelled / Completed/CANCELLED_BY_OPERATOR / 释放 2026-10-18T04:23:34.5224096+00:00` |
| 终态没有仓位命令、没有动过的门：这条需求 0 笔仓位操作、全程 0 条 SlotOperationCommand、0 条录入提交，那条没人回答的录入请求已结算，没有去关卡的单，RIoT 上只有那一张取货单，没有 RecoveryRequired（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE；forbidden: duplicate-riot-order、duplicate-business-commit） | PASS | `操作 0 / 命令 0 / 提交 0 / 录入请求已结算 / TO_GATE 0 / RIoT 单 1 / RecoveryRequired 0` | `操作 0 / 命令 0 / 提交 0 / 录入请求已结算=True / TO_GATE 0 / RIoT 单 1 / RecoveryRequired 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
