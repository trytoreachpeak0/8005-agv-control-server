# L2 场景证据：binding-hold-dashboard-not-cascading

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161825921Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161825921Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 确认页不自动刷新，带着这一行的 Map 与任务类型；提交后 303 回到看板主页 | PASS | `no refresh, taskType=STAGING_TO_WIRE, 303 → /` | `refresh=False, 303 → /` |
| 暂停只落在 Map 25 的 STAGING_TO_WIRE 上，来源看板人工；WIRE_TO_GATE 没有暂停 | PASS | `STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD` | `STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD` |
| STAGING_TO_WIRE 暂停期间，WIRE_TO_GATE 需求照常受理并走完两段（不连带） | PASS | `ACCEPTED or DEMAND_ALREADY_ACCEPTED → Completed` | `DEMAND_ALREADY_ACCEPTED → Completed` |
| 看板主页 STAGING_TO_WIRE 那一行显示已暂停、来源看板人工与理由；WIRE_TO_GATE 那一行正常 | PASS | `STAGING_TO_WIRE: 已暂停 看板人工 …；WIRE_TO_GATE: 正常` | `STAGING_TO_WIRE 在本图需求集合 230 派工待送取货 已暂停 看板人工，自 2026-10-09T16:18:56.9571614+00:00，派工待送取货点被料车占住 暂停 \| WIRE_TO_GATE 在本图需求集合 210 关卡 正常 无 暂停` |
| 暂停之前两趟都已受理、在两台不同的车上：second 已建关卡单，pending 在去取货点的路上、还没有关卡单 | PASS | `two vehicles; pending has no TO_GATE intent` | `second on AGV-L2-002, pending on AGV-L2-001; pending TO_GATE: none` |
| 经看板再暂停 WIRE_TO_GATE：提交 303，两条人工暂停各落在自己的任务类型上 | PASS | `303; STAGING_TO_WIRE/MANUAL, WIRE_TO_GATE/MANUAL` | `303; STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD, WIRE_TO_GATE/MANUAL/DASHBOARD_MANUAL_HOLD` |
| 暂停之后已建的关卡单不改单、不换站、不取消：同一个 UpperId 与 OrderId，仍是 CONFIRMED | PASS | `W2G-aee4be38-3a8f-4717-88b1-f90663c2e9f1-GATE-1 / ORDER-000004 / CONFIRMED` | `W2G-aee4be38-3a8f-4717-88b1-f90663c2e9f1-GATE-1 / ORDER-000004 / CONFIRMED` |
| 暂停前已建关卡单的那一趟照常走完 | PASS | `Completed` | `Completed` |
| 暂停时尚未建单的关卡腿不建：没有 TO_GATE 单，旅程阻断原因为已暂停，这趟只在 RIoT 上建过取货单 | PASS | `block TASK_TYPE_HELD, no TO_GATE intent, 1 RIoT order` | `block 'TASK_TYPE_HELD' at AwaitingDepartureSafety, TO_GATE: none, RIoT orders: 1` |
| 有空车时，WIRE_TO_GATE 暂停后的新需求不受理，JourneyBacklog 原因为已暂停 | PASS | `no journey / TASK_TYPE_HELD` | `no journey / TASK_TYPE_HELD` |
| 看板主页没有表单、没有指向解除的链接，本票卡片里没有解除字样；看板与服务端的解除请求都不是成功响应，两条暂停仍然成立 | PASS | `no form, no lifting link, no 解除 in the binding card; every release attempt >= 400; 2 holds standing` | `form: False; lifting links: ; 解除 in card: False; statuses 404, 405, 404; STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD, WIRE_TO_GATE/MANUAL/DASHBOARD_MANUAL_HOLD` |
| 暂停不改绑定：生效绑定集版本与每条绑定前后相同 | PASS | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
