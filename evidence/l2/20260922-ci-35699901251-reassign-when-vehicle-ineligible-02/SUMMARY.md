# L2 场景证据：reassign-when-vehicle-ineligible

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T075314174Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T075314174Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 主车接下需求，正开往取货站（取货单已确认） | PASS | `AGV-L2-001` | `AGV-L2-001` |
| 线上收到一条 CMD_ORDER_CANCEL，打在主车那张取货单上 | PASS | `CMD_ORDER_CANCEL @ ORDER-000001` | `1 条` |
| 释放：归属标 RELEASED_FOR_REDISPATCH，主车的旅程关闭（Completed / RELEASED_FOR_REDISPATCH） | PASS | `RELEASED_FOR_REDISPATCH / Completed / RELEASED_FOR_REDISPATCH` | `RELEASED_FOR_REDISPATCH / Completed / RELEASED_FOR_REDISPATCH` |
| 取消只发了一次，审计行对账结果是 Confirmed（结果未知不释放） | PASS | `1 / Confirmed` | `1 / Confirmed` |
| 需求回到调度后由第二台车受理：新旅程、新代次、新 upperId（旧 upperId 不复用） | PASS | `AGV-L2-002 / upperId ≠ W2G-8c970711-0c3b-4606-9329-283e18a394c8-PICKUP-1 / 代次 > 1` | `AGV-L2-002 / W2G-8c970711-0c3b-4606-9329-283e18a394c8-PICKUP-2 / 代次 2` |
| 旧 upperId 只属于第一趟那一张订单，没有出现在任何新订单上 | PASS | `1` | `1` |
| 等待年龄没有重置：积压行的 FirstSeenAt 与释放之前相同，再受理后又带上受理时刻 | PASS | `2026-09-22 07:53:31.528185+00:00 / 有受理时刻` | `2026-09-22 07:53:31.528185+00:00 / 有受理时刻` |
| 第二趟派往取货站的那一版计划发出了，带着新取货腿（没有被「已发过」吞掉） | PASS | `≥ 1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
