# L2 场景证据：vehicle-fault-operator-clearance

结论：**FAIL**

失败原因：The property 'outcome' cannot be found on this object. Verify that the property exists.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T135900101Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `80a1286814af9733bbe621065e1375f5ee3b8365` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260922T135900101Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 急停锁着：清除入口回 409，理由点名 FAULT_RECOVERY_EMERGENCY_LATCHED；故障仍是 SuspectedBlocked，没有发 cancelEmergency（清除不解除急停） | FAIL | `409 / FAULT_RECOVERY_EMERGENCY_LATCHED / SuspectedBlocked / 0 次` | `404 /  / SuspectedBlocked / 0 次` |
| 急停解除之后故障仍在（解除只结束急停）：SuspectedBlocked、未清除；车一直挂在原旅程上，没有新旅程 | PASS | `SuspectedBlocked / 1 趟旅程` | `SuspectedBlocked / 1 趟旅程` |
| 没确认故障已排除、RIoT 里这台车还有未结束的订单：入口回 409，三条理由（没确认、车上有未完成订单、当前单没结束）一起列出；故障不动 | FAIL | `409 / REMEDY_NOT_CONFIRMED + VEHICLE_ORDER_NOT_FINISHED + CURRENT_ORDER_NOT_ENDED / SuspectedBlocked` | `404 /  / SuspectedBlocked` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
