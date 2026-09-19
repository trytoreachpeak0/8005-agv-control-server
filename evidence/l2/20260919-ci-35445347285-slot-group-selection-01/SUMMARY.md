# L2 场景证据：slot-group-selection

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T132250394Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35445347285-1\_stage\l2-20260919T132250394Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 库内整车模型的分组只有 FRONT 与 REAR，本车的仓位模型可解析 | PASS | `SlotModelSlots: FRONT,REAR; vehicle groups: FRONT,REAR` | `SlotModelSlots: FRONT,REAR; vehicle groups: FRONT,REAR` |
| 需求要 3 个花篮，旅程恰好占 3 个目标仓 | PASS | `ExpectedBasketCount 3, 3 target slots` | `ExpectedBasketCount 3, 3 target slots [5,6,7]` |
| N1-3 指 REAR：目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 3 个可用仓 | PASS | `[5,6,7]（REAR 组最小的可用仓，升序）` | `[5,6,7] on AGV-L2-001` |
| 第一条需求的旅程走到 Completed | PASS | `Completed` | `Completed` |
| 取货点下发给车的 LOAD 命令 slots 与旅程目标仓相同 | PASS | `[5,6,7]` | `1 command(s), slots: [5,6,7]` |
| 关卡下发给车的 UNLOAD 命令 slots 与旅程目标仓相同 | PASS | `[5,6,7]` | `1 command(s), slots: [5,6,7]` |
| 导入把 N1-3 改指 FRONT 的新版本归属表 | PASS | `OK, version 2` | `OK, version 2` |
| 第二条需求同样要 3 个花篮 | PASS | `3` | `3` |
| N1-3 改指 FRONT 之后：目标仓全部属于本车 FRONT 组、升序，且恰好是该组编号最小的 3 个可用仓 | PASS | `[1,2,3]（FRONT 组最小的可用仓，升序）` | `[1,2,3] on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
