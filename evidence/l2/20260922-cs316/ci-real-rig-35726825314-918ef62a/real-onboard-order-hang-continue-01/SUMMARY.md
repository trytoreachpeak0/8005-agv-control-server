# L2 场景证据：real-onboard-order-hang-continue

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T122421120Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `918ef62a4687bc10af328b0896d819981f3e6ad3` |
| onboardHmiCommit | `083fd9a76e5cf20c96e13ed6ea0140249a471d48` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35726825314-1\_stage\l2-20260922T122421120Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：车在途时真车载端会话掉出 Ready，闸门先为旅程写了 ONBOARD_SESSION_NOT_READY（挂起在这之后才注入） | PASS | `ONBOARD_SESSION_NOT_READY` | `ONBOARD_SESSION_NOT_READY` |
| 真车载端在场时，订单挂起后旅程写 ORDER_HANG | PASS | `ORDER_HANG` | `ORDER_HANG` |
| 前提：挂起期间会话未就绪（真车载端看到本服务端自己的在途单），所以这条走的是会话闸门那条路 | PASS | `非 Ready` | `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` |
| 前提仍然成立：几轮之后会话仍未就绪，ORDER_HANG 在这段时间里一直是闸门那条路维持的 | PASS | `非 Ready` | `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` |
| 挂起期间没有订单命令、急停或故障事实；码与开始时刻不变 | PASS | `0 / 0 / 0 / ORDER_HANG / 2026-09-22 12:25:07.9335972+00:00` | `0 / 0 / 0 / ORDER_HANG / 2026-09-22 12:25:07.9335972+00:00` |
| continue 之后码回到闸门的 ONBOARD_SESSION_NOT_READY（会话仍未就绪），旅程仍在开往取货站 | PASS | `AwaitingPickupArrival / ONBOARD_SESSION_NOT_READY` | `AwaitingPickupArrival / ONBOARD_SESSION_NOT_READY` |
| 到站后旅程照常推进，车载端允许录入 sublot；全程没有订单命令、急停或故障事实 | PASS | `AwaitingSublot / 0 / 0 / 0` | `AwaitingSublot / 0 / 0 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
