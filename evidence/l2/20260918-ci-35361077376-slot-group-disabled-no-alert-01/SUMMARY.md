# L2 场景证据：slot-group-disabled-no-alert

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T152515084Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35361077376-1\_stage\l2-20260918T152515084Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：禁用的 [1,2] 属于本车 FRONT 组；组内可用仓少于 3 个，组的物理仓位数不少于 3 个 | PASS | `FRONT physical [1,2,3,4] >= 3, available in group < 3, disabled not available` | `FRONT physical [1,2,3,4], available in group [3,4], all available [3,4,5,6,7,8]` |
| 积压原因为「所需分组暂时空仓不足」，再过三轮仍是 | PASS | `SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE` | `first SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE, after three rounds SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE` |
| 不受理：没有受理行、没有旅程、没有建 RIoT 单 | PASS | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` | `AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0` |
| StructuralDispatchBlocks 没有这条需求的行（含已清除的）：仓位临时禁用不是结构性阻断 | PASS | `0` | `0` |
| 服务端日志里没有这条需求的「结构性派车阻断形成」记录 | PASS | `0 records` | `0 records` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
