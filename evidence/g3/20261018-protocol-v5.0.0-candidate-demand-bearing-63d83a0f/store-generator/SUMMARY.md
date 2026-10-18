# L2 场景证据：demand-bearing-store-at-unload

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261018T041424880Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `63d83a0f623a99d0260c2b1c95672a91584336b5` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261018T041424880Z` |
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
