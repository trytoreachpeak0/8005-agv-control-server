# L2 场景证据：same-direction-binding-missing-not-cascading

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T110107069Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-10` |
| controlServerCommit | `8b780a24d92e7b141b1c0106a706da1171a13281` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T110107069Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| WIRE_TO_GATE（已绑定、最后发布）被受理：派车轮已经跑过，排在它前面的四条同向需求已经被判过 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 四类都不受理：积压原因都是 TASK_TYPE_BINDING_MISSING，没有受理时刻，没有受理行、旅程、旅程归属、订单意图 | PASS | `DIE_TO_WIRE_STAGING=TASK_TYPE_BINDING_MISSING rows=0; DIE_TO_OVEN=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_OPTICAL=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_NITROGEN=TASK_TYPE_BINDING_MISSING rows=0` | `DIE_TO_WIRE_STAGING=TASK_TYPE_BINDING_MISSING rows=0; DIE_TO_OVEN=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_OPTICAL=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_NITROGEN=TASK_TYPE_BINDING_MISSING rows=0` |
| /api/dashboard/dispatch-backlog 四条都列出，原因码 TASK_TYPE_BINDING_MISSING 带中文说明 | PASS | `four rows / TASK_TYPE_BINDING_MISSING / Chinese description` | `DIE_TO_WIRE_STAGING: 1 row(s) TASK_TYPE_BINDING_MISSING +zh; DIE_TO_OVEN: 1 row(s) TASK_TYPE_BINDING_MISSING +zh; WIRE_TO_OPTICAL: 1 row(s) TASK_TYPE_BINDING_MISSING +zh; WIRE_TO_NITROGEN: 1 row(s) TASK_TYPE_BINDING_MISSING +zh` |
| StructuralDispatchBlocks 没有这四条的行（含已清除的）：缺绑定是配置造成的不投运，不是结构性告警 | PASS | `0` | `0` |
| WIRE_TO_GATE 在同一次运行内被受理并走完两段：同向四类缺绑定不连带它 | PASS | `Completed / gate leg to 210 / 2 RIoT orders` | `Completed / gate leg to 210 / 2 RIoT orders` |
| 车空下来之后又转三轮：四条仍不受理，原因仍是 TASK_TYPE_BINDING_MISSING | PASS | `DIE_TO_WIRE_STAGING=TASK_TYPE_BINDING_MISSING rows=0; DIE_TO_OVEN=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_OPTICAL=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_NITROGEN=TASK_TYPE_BINDING_MISSING rows=0` | `DIE_TO_WIRE_STAGING=TASK_TYPE_BINDING_MISSING rows=0; DIE_TO_OVEN=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_OPTICAL=TASK_TYPE_BINDING_MISSING rows=0; WIRE_TO_NITROGEN=TASK_TYPE_BINDING_MISSING rows=0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
