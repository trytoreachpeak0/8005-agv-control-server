# L2 场景证据：real-onboard-in-transit-door-facts

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260928T174924986Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `5ee337fd4fbfb6fc2fcc77979898757fbbdb4807` |
| onboardHmiCommit | `60f341878a1bce705598aa9043234c1ca2fb1d7a` |
| protocolFaultProxy | `True` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-36459665922-1\_stage\l2-20260928T174924986Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 真车载端空车行驶 12 秒（会话因本单未就绪）：不 Hold、不急停、不 Cancel | PASS | `>= 10 samples, 0/0/0` | `24 samples, 0/0/0` |
| 真车载端有货行驶 12 秒并途中重连一次：不 Hold、不急停、不 Cancel | PASS | `>= 10 samples, 0/0/0` | `23 samples, 0/0/0` |
| 锁反馈变 0：Hold 恰好一条，打在本车、关卡段这一张单上，RIoT 侧收到的 CMD_ORDER_HELD 也打在这张 orderId 上 | PASS | `1 / AGV-L2-001 / W2G-0bd13701-4315-4e88-9034-7c18d28498ae-GATE-1 / -> ORDER-000002` | `1 / AGV-L2-001 / W2G-0bd13701-4315-4e88-9034-7c18d28498ae-GATE-1 / -> ORDER-000002` |
| 按不住、车还在动：急停恰好一条，打在这台车上，而且在 Hold 之后 | PASS | `1 / AGV-L2-001 / hold first` | `1 / AGV-L2-001 / hold 2026-09-28 17:50:55.5504045+00:00 trigger 2026-09-28 17:50:56.0158096+00:00` |
| 故障事实记的是门锁症状 | PASS | `VEHICLE_DOOR_NOT_PROVEN_LOCKED` | `VEHICLE_DOOR_NOT_PROVEN_LOCKED` |
| 锁反馈仍是 0 时不解除 | PASS | `0` | `0` |
| 锁反馈恢复后自动解除一次，原因记门锁原因消除 | PASS | `1 / EMERGENCY_DOOR_CAUSE_REMOVED` | `1 / EMERGENCY_DOOR_CAUSE_REMOVED` |
| 解除之后单仍停着：没有 CONTINUE，没有第二次急停，没有 Cancel | PASS | `0 continue / 1 trigger / 0 cancel` | `0 continue / 1 trigger / 0 cancel` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
