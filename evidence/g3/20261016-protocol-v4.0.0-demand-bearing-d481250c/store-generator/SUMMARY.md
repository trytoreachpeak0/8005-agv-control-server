# L2 场景证据：demand-bearing-store-at-unload

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T072012300Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d481250c08d65b9b03ba854f22f563fb33bfa5aa` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `4.0.0` |
| protocolReleaseIdentity.tag | `protocol-v4.0.0` |
| protocolReleaseIdentity.commit | `3fb214e57d861a909d872511940d26133eb32dca` |
| protocolReleaseIdentity.protocolVersion | `5` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a` |
| protocolReleaseIdentity.schemaBundleSha256 | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T072012300Z` |
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
