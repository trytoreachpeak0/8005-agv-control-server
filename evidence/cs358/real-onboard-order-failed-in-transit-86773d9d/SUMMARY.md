# L2 场景证据：real-onboard-order-failed-in-transit

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260926T160359204Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `86773d9d685052bc7ba50857304c5ea781cbc1e9` |
| onboardHmiCommit | `b789c3d4a249cd3368aa53ea5f7ebe992d109666` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260926T160359204Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：车在途时真车载端会话掉出 Ready，闸门先为旅程写了 ONBOARD_SESSION_NOT_READY（FAILED 在这之后才注入） | PASS | `ONBOARD_SESSION_NOT_READY` | `ONBOARD_SESSION_NOT_READY` |
| 订单 FAILED、车在动：记下 SuspectedBlocked 故障，证据码 VEHICLE_ORDER_FAILED，并已升级 | PASS | `SuspectedBlocked / VEHICLE_ORDER_FAILED / 有升级时刻` | `SuspectedBlocked / VEHICLE_ORDER_FAILED / 2026-09-26 16:04:30.7865369+00:00` |
| 命令审计里有 OrderHold 与 triggerEmergency，假 RIoT 收到了急停触发 | PASS | `OrderHold>=1 / triggerEmergency>=1 / RIoT 急停调用>=1` | `1 / 2 / 2` |
| 旅程码是 VEHICLE_ORDER_FAILED，没被闸门的 ONBOARD_SESSION_NOT_READY 盖掉 | PASS | `VEHICLE_ORDER_FAILED` | `VEHICLE_ORDER_FAILED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
