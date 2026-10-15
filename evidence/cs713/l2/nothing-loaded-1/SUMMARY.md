# L2 场景证据：staging-depart-with-nothing-loaded

结论：**FAIL**

失败原因：Conversion from JSON failed with error: Additional text encountered after finished reading JSON content: {. Path '', line 1, position 829.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261015T024359840Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `fc014e3e23ccaa69b47b0542eea7ae788a64cf69` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `4.0.0` |
| protocolReleaseIdentity.tag | `protocol-v4.0.0` |
| protocolReleaseIdentity.commit | `4d67655c4da5120d20cd06d99f67f14c4f2dfb08` |
| protocolReleaseIdentity.protocolVersion | `5` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `38a5d2d53e1e235f7f844d991c7258ed51910d542f235ebae6768455c5511fc4` |
| protocolReleaseIdentity.schemaBundleSha256 | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| protocolReleaseIdentity.vectorsSha256 | `290b9260df46cd484f993a5a5022712fd54e043c9777da78caa0f9aabf5aa300` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261015T024359840Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 出发请求得到 CurrentStopDepartureResult / ACCEPTED | PASS | `ACCEPTED` | `ACCEPTED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
