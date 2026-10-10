# L2 场景证据：demand-bearing-store-at-unload

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T130752789Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `448ab5bf3e479264f22ef78e83f73f2f0de07c90` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261009T130752789Z` |
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
