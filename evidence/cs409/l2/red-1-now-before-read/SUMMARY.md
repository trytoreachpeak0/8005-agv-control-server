# L2 场景证据：charging-clearance-to-waiting-point

结论：**FAIL**

失败原因：Timed out after 60s waiting for: A queued with no eligible waiting point. Last observed: "AwaitingPickupArrival CHARGING_CLEARANCE_VEHICLE_OFF_CHARGER"

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T080948108Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `92585a2ee424a31fbb580b1cf7d2fbc61fb33bba` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261002T080948108Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| A 在 211 上充不上（407802 + HANG）：桩暂停（UNABLE_TO_CHARGE_CONFIRMED，未恢复），周期清桩中，A 的用途 CLEARING_MAINTENANCE；B 收敛占着 214 | PASS | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T081041038Z / 214 OCCUPIED BROKERX-L2-0002` | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| CLEARING \| CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261002T081041038Z / 214 OCCUPIED BROKERX-L2-0002 idle-return:BROKERX-L2-0002:20261002T081037676Z` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
