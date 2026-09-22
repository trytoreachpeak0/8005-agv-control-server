# L2 场景证据：starvation-threshold-escalation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T075837834Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T075837834Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 阈值未配置：等了 5 分钟的 WIRE_TO_GATE 不升级，车空出来先受理 STAGING_TO_WIRE（REQ-0203 降级：只计龄、不升级） | PASS | `STAGING1` | `STAGING1` |
| 阈值未配置：受理 STAGING1 那一轮结束之后，HUNGRY 没有升级告警标记，服务端日志里也没有它的升级告警 | PASS | `round over, no mark, 0 records` | `rejudged True, mark '', 0 record(s)` |
| 导入 60 秒阈值之后 HUNGRY 被标记升级，记下的参数版本是刚导入的那一版 | PASS | `escalated under version 1` | `import OK v1; mark '2026-09-22 07:59:34.568656+00:00' version '1'` |
| 服务端日志里恰好一条 HUNGRY 的升级告警 | PASS | `1` | `1` |
| 超时的 HUNGRY 越过刚建、未超时的 STAGING_TO_WIRE 先被受理（REQ-0202：超时层高于所有未超时的带） | PASS | `HUNGRY` | `HUNGRY` |
| HUNGRY 从告警到受理隔了好几轮：告警标记时刻没变，日志里仍只有一条（按任务去重） | PASS | `mark 2026-09-22 07:59:34.568656+00:00, 1 record` | `mark '2026-09-22 07:59:34.568656+00:00', 1 record(s)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
