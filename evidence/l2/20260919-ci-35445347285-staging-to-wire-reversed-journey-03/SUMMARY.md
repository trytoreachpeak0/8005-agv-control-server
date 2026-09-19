# L2 场景证据：staging-to-wire-reversed-journey

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T133128484Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35445347285-1\_stage\l2-20260919T133128484Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 反向旅程的取货站是派工待送站，卸货站是需求 AREA 的机台站 | PASS | `pickup 305, drop-off 12` | `pickup 305, drop-off 12` |
| 计划快照先 TO_PICKUP 到派工待送站、后 TO_DROPOFF 到机台站，publicStationFunction 为空 | PASS | `1:TO_PICKUP@派工待送取货 2:TO_DROPOFF@N1-3_N1-7, no publicStationFunction` | `1:TO_PICKUP@派工待送取货 2:TO_DROPOFF@N1-3_N1-7; publicStationFunction: null,null` |
| STAGING_TO_WIRE 旅程先装后卸走到 Completed | PASS | `Completed` | `Completed` |
| 两条移动订单的目标站依次是派工待送站与机台站 | PASS | `305,12` | `305,12` |
| 清单快照：派工待送站 stopRole 为 PICKUP，机台站为 DROPOFF | PASS | `派工待送取货:PICKUP N1-3_N1-7:DROPOFF` | `派工待送取货:PICKUP N1-3_N1-7:DROPOFF` |
| 准入决策只冻结在卸货那次操作上，站点是机台站、任务类型 STAGING_TO_WIRE | PASS | `one decision on UnloadSlotOperationAttemptId: N1-3_N1-7/STAGING_TO_WIRE` | `1 decision(s): Unload:N1-3_N1-7/STAGING_TO_WIRE/1` |
| WIRE_TO_GATE 照常：机台站取货、关卡卸货、走完，准入冻结在装货那次操作上 | PASS | `pickup 12, drop-off 210, Completed, one decision on the load` | `pickup 12, drop-off 210, Completed, 1 decision(s): Load:N1-3_N1-7/WIRE_TO_GATE` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
