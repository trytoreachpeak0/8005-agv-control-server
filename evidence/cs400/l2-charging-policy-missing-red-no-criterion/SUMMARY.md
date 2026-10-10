# L2 场景证据：charging-policy-missing-vehicle-not-commissioned

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260929T160851326Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `b582d6383a5b5b8def06be35aed5029feba19a26` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260929T160851326Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：唯一一版已激活的策略经 FieldOps 导入、以 L2_PRESET 批准，适用范围只有 A | PASS | `1 行 / BROKERX-L2-0002 / L2_PRESET` | `1/BROKERX-L2-0002/L2_PRESET` |
| 需求派给策略范围内的 A，不是轮次里排在前面的 B | FAIL | `BROKERX-L2-0002 / AGV-L2-002` | `BROKERX-L2-0001 / AGV-L2-001` |
| B 被派车链以 CHARGING_POLICY_NOT_APPROVED 挡下（服务端日志） | FAIL | `Vehicle BROKERX-L2-0001 takes no new work: CHARGING_POLICY_NOT_APPROVED` | `not found` |
| A 的取货单已确认，而 B 在整个窗口里没有任何建单（库里的订单意图、合成 RIoT 的订单） | FAIL | `CONFIRMED / BROKERX-L2-0002 / B: 0 intents, 0 RIoT orders` | `CONFIRMED / BROKERX-L2-0001 / B: 1 intents, 1 RIoT orders` |
| 逐车判定、不整机拒绝启动：服务端进程在跑、存活检查 200 | PASS | `200` | `200` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
