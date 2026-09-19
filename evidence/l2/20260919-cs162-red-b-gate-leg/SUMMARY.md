# L2 场景证据：binding-hold-dashboard-not-cascading

结论：**FAIL**

失败原因：Timed out after 180s waiting for: the pending journey stops before its gate leg because WIRE_TO_GATE is held. Last observed: {"Stage":"AwaitingGateArrival","AgvId":"AGV-L2-002","VehicleKey":"BROKERX-L2-0002","BlockReasonCode":null,"PickupStationRiotId":12,"GateStationRiotId":210,"GateUpperId":"W2G-a82a264b-9e9b-41dd-9440-b68a4b9bd6ca-GATE-1"}

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T071256041Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `e77db781e292e122939f5857bb8da4402ad47c67` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T071256041Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 确认页不自动刷新，带着这一行的 Map 与任务类型；提交后 303 回到看板主页 | PASS | `no refresh, taskType=STAGING_TO_WIRE, 303 → /` | `refresh=False, 303 → /` |
| 暂停只落在 Map 25 的 STAGING_TO_WIRE 上，来源看板人工；WIRE_TO_GATE 没有暂停 | PASS | `STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD` | `STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD` |
| STAGING_TO_WIRE 暂停期间，WIRE_TO_GATE 需求照常受理并走完两段（不连带） | PASS | `ACCEPTED or DEMAND_ALREADY_ACCEPTED → Completed` | `DEMAND_ALREADY_ACCEPTED → Completed` |
| 看板主页 STAGING_TO_WIRE 那一行显示已暂停、来源看板人工与理由；WIRE_TO_GATE 那一行正常 | PASS | `STAGING_TO_WIRE: 已暂停 看板人工 …；WIRE_TO_GATE: 正常` | `STAGING_TO_WIRE 在本图需求集合 230 派工待送取货 已暂停 看板人工，自 2026-09-19T07:14:37.9134186+00:00，派工待送取货点被料车占住 暂停 \| WIRE_TO_GATE 在本图需求集合 210 关卡 正常 无 暂停` |
| 暂停之前两趟都已受理、在两台不同的车上：second 已建关卡单，pending 在去取货点的路上、还没有关卡单 | PASS | `two vehicles; pending has no TO_GATE intent` | `second on AGV-L2-001, pending on AGV-L2-002; pending TO_GATE: none` |
| 经看板再暂停 WIRE_TO_GATE：提交 303，两条人工暂停各落在自己的任务类型上 | PASS | `303; STAGING_TO_WIRE/MANUAL, WIRE_TO_GATE/MANUAL` | `303; STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD, WIRE_TO_GATE/MANUAL/DASHBOARD_MANUAL_HOLD` |
| 暂停之后已建的关卡单不改单、不换站、不取消：同一个 UpperId 与 OrderId，仍是 CONFIRMED | PASS | `W2G-b2e368a2-5093-4330-9142-472ee7efbb99-GATE-1 / ORDER-000004 / CONFIRMED` | `W2G-b2e368a2-5093-4330-9142-472ee7efbb99-GATE-1 / ORDER-000004 / CONFIRMED` |
| 暂停前已建关卡单的那一趟照常走完 | PASS | `Completed` | `Completed` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
