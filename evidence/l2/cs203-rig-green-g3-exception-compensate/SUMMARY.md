# L2 场景证据：g3-exception-compensate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T175748044Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `9dbd6dec01ee31d9368224fc3937abbd86c9becb` |
| onboardHmiCommit | `08569c4f2f83a8f13b734d161055351c2621537a` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260920T175748044Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：SessionRequested → Opened → ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted → LoadCompensationRequested → LoadCompensationCommand → LoadCompensationResult（CV-EXCEPTION-COMPENSATE orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `SessionRequested×1 / Action×1 COMPENSATE_LOAD_ALL_EMPTY / CompensationRequested×1 / Command×1 / Result×1 / 有序=True` |
| 补偿依托恢复会话授权：工作流挂在这次打开的恢复会话上，命令就是工作流绑定的那一条，指向同一会话、同一尝试、同一仓位（AUTHORIZE_COMPENSATION_AGAINST_RECOVERY_SESSION） | PASS | `会话 ea385bdb-a4a5-5052-b56b-114c2145e691 / 命令 = 工作流绑定 / attempt da9bd39e-118f-3c50-bfad-b46ed874dbed / 仓 1` | `会话 ea385bdb-a4a5-5052-b56b-114c2145e691 / 命令 1f5273eb-313b-fd53-a99b-ca3303063a15 vs 绑定 1f5273eb-313b-fd53-a99b-ca3303063a15 / attempt da9bd39e-118f-3c50-bfad-b46ed874dbed / 仓 1` |
| 补偿只执行一次、证空不开门：一条命令、一份结果；按下补偿之后没有任何开锁（EXECUTE_COMPENSATION_ONCE / forbidden duplicate-slot-unlock） | PASS | `命令 1 / 结果 1 / 补偿后开锁 0` | `命令 1 / 结果 1 / 补偿后开锁 0` |
| 车载端报补偿后的仓位状态：ALL_EMPTY，每仓空、锁上、输出复位，与模拟器一致（REPORT_COMPENSATED_SLOT_STATE） | PASS | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0` | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0` |
| 补偿收敛且无重复提交：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾，没有去关卡，RIoT 上只有取货那一张单（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE） | PASS | `Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / TO_GATE 0 / RIoT 单 1` | `Reconciled / CLOSED / Cancelled / Cancelled / 释放=True / Completed/CANCELLED_BY_LOAD_COMPENSATION / TO_GATE 0 / RIoT 单 1` |
| 补偿收敛之后车辆放出来了：这条需求的 TO_PICKUP 单车辆占用已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival，不是 Blocked/VEHICLE_OCCUPANCY_CONFLICT；control-server#131） | PASS | `TO_PICKUP 占用已释放 / 下一单 AwaitingPickupArrival on AGV-L2-001，TO_PICKUP 意图 CONFIRMED，没有停摆原因码` | `VehicleOccupancyReleasedAt='2026-09-20 17:58:18.4549181+00:00' / 下一单 AwaitingPickupArrival/ TO_PICKUP=CONFIRMED on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
