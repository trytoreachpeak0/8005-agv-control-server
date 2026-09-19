# L2 场景证据：task-type-binding-missing-not-cascading

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T133528952Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `50dc987d680a33c3f8732619a91f5761453ddd63` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35445347285-1\_stage\l2-20260919T133528952Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| WIRE_TO_GATE 在同一次运行内被受理并走完两段：另一个任务类型缺绑定不连带它 | PASS | `Completed / gate leg to 210 / 2 RIoT orders` | `Completed / gate leg to 210 / 2 RIoT orders` |
| STAGING_TO_WIRE 未受理：积压原因 TASK_TYPE_BINDING_MISSING，没有受理行、旅程、订单意图 | PASS | `TASK_TYPE_BINDING_MISSING / not accepted / 0 rows` | `TASK_TYPE_BINDING_MISSING / AcceptedAt= / 0 rows` |
| /api/dashboard/dispatch-backlog 列出这条 STAGING_TO_WIRE 需求，原因码 TASK_TYPE_BINDING_MISSING 带中文说明 | PASS | `one row / TASK_TYPE_BINDING_MISSING / Chinese description` | `TASK_TYPE_BINDING_MISSING / 本图没有为这个任务类型绑定固定站点（不在本图需求集里，或在需求集里却没绑定），只有这个任务类型不投运` |
| StructuralDispatchBlocks 没有这条 STAGING_TO_WIRE 需求的行：缺绑定是配置造成的不投运，不是结构性告警 | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
