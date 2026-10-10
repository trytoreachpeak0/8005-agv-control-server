# L2 场景证据：task-type-binding-missing-not-cascading

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163443639Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163443639Z-slot3` |
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
