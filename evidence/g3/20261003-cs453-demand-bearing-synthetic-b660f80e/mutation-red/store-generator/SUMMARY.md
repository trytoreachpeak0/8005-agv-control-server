# L2 场景证据：demand-bearing-store-at-unload

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261003T043100565Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `01ed0100d939655d0bd90ddc57e0f21fcf0d9012` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261003T043100565Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 导出的库里恰好一条 Prepared 的卸货操作、至少一条 Committed 的装货操作 | PASS | `Prepared Unload ×1 / Committed Load ≥1` | `Load:Committed, Unload:Prepared` |
| 卸货结果没回（结果只有装货那一条），一辆车一行会话恢复 | PASS | `1 result / 1 session row (AGV-L2-001)` | `1 results / 1 session rows (AGV-L2-001)` |
| 需求已受理未收尾，三张收尾表都是空的 | PASS | `Accepted / UnloadBatches=0,StopClosures=0,TransportDemandCompletions=0` | `Accepted / UnloadBatches=0,StopClosures=0,TransportDemandCompletions=0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
