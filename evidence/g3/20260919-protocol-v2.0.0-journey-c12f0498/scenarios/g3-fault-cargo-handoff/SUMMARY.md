# L2 场景证据：g3-fault-cargo-handoff

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T175253662Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `c12f0498281a39e4fa94a51bd4506c3d3af97a1c` |
| onboardHmiCommit | `29fbf65e0b4d58c80849d5e6d0e44f40903c411e` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260918T175253662Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：RecoveryActionSubmitted(FAULT_CARGO_HANDOFF) → RecoveryActionAccepted → FaultCargoRecoveryCommand → FaultCargoRecoveryResult（CV-FAULT-CARGO-HANDOFF orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `Action×1 RecoveryActionAccepted FAULT_CARGO_HANDOFF / Command×1 / Result×1 / 有序=True` |
| 服务端记下这次交接：工作流带交接号与结果 HANDED_OFF，状态 Reconciled（RECORD_FAULT_CARGO_HANDOFF） | PASS | `交接号已记 / HANDED_OFF / Reconciled` | `交接号 832ca8b9-b1ef-4751-a8c7-3e641766f81f / HANDED_OFF / Reconciled` |
| 只凭授权的命令交接：命令就是工作流绑定的那一条，命令、结果、工作流三处交接号相同；按下交接之后没有额外开锁（HANDOFF_ONLY_ON_AUTHORIZED_COMMAND / forbidden duplicate-slot-unlock） | PASS | `命令 = 绑定 / 交接号 832ca8b9-b1ef-4751-a8c7-3e641766f81f ×3 / 开锁 0` | `命令 4ae1c54a-9643-5554-991c-e2435a4d75c8 / 命令交接号 832ca8b9-b1ef-4751-a8c7-3e641766f81f / 结果交接号 832ca8b9-b1ef-4751-a8c7-3e641766f81f / 工作流 832ca8b9-b1ef-4751-a8c7-3e641766f81f / 开锁 0` |
| 车载端报交接结果：HANDED_OFF，每个授权仓空、锁上、输出复位（REPORT_HANDOFF_OUTCOME） | PASS | `HANDED_OFF 1=COMPLETED/EMPTY/LOCKED/RESET` | `HANDED_OFF 1=COMPLETED/EMPTY/LOCKED/RESET` |
| 交接收敛且无重复提交：需求与装载 Cancelled，旅程以 TERMINATED_BY_FAULT_CARGO_HANDOFF 收尾、没有去关卡，RIoT 上只有取货那一张单，仓位物理上空、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE） | PASS | `Cancelled / Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / TO_GATE 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0` | `Cancelled / Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / TO_GATE 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0` |
| 交接收敛之后车辆放出来了：这条需求的 TO_PICKUP 单车辆占用已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival，不是 Blocked/VEHICLE_OCCUPANCY_CONFLICT；control-server#131） | PASS | `TO_PICKUP 占用已释放 / 下一单 AwaitingPickupArrival on AGV-L2-001` | `VehicleOccupancyReleasedAt='2026-09-18 17:53:24.3375997+00:00' / 下一单 AwaitingPickupArrival/ on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
