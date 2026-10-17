# L2 场景证据：area-eqp-unique-across-task-types

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T144056054Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `79aa158dca41f494de1f20485a5f59ebd1e562d4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `13eca82ac199bd97c1ad49d036c70cb5b012250f` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `128daae52758749d4d6f346231fdb336ca5d1079773bcab5430c20c53ce6b10b` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T144056054Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| WIRE_TO_GATE 候选未受理：同 AREA 在另一个任务类型的行里对应另一台 EQP，原因 AREA_EQP_NOT_UNIQUE | PASS | `AREA_EQP_NOT_UNIQUE / not accepted / 0 rows` | `AREA_EQP_NOT_UNIQUE / AcceptedAt= / 0 rows` |
| 删掉其它任务类型那一行后，WIRE_TO_GATE 在后续一轮被受理并派去取货点 | PASS | `AwaitingPickupArrival / ACCEPTED (or DEMAND_ALREADY_ACCEPTED written after AcceptedAt) / AcceptedAt set and = AcceptedDemands.AcceptedAt` | `AwaitingPickupArrival / ReasonCode=ACCEPTED / AcceptedAt=2026-10-17 14:42:53.7347635+00:00 / LastSeenAt=2026-10-17 14:42:53.7347635+00:00 / AcceptedDemands.AcceptedAt=2026-10-17 14:42:53.7347635+00:00` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
