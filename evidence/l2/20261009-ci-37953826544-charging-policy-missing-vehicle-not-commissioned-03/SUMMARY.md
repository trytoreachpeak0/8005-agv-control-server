# L2 场景证据：charging-policy-missing-vehicle-not-commissioned

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161619457Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161619457Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：唯一一版已激活的策略经 FieldOps 导入、以 L2_PRESET 批准，适用范围只有 A | PASS | `1 行 / BROKERX-L2-0002 / L2_PRESET` | `1/BROKERX-L2-0002/L2_PRESET` |
| 需求派给策略范围内的 A，不是轮次里排在前面的 B | PASS | `BROKERX-L2-0002 / AGV-L2-002` | `BROKERX-L2-0002 / AGV-L2-002` |
| B 被派车链以 CHARGING_POLICY_NOT_APPROVED 挡下（服务端日志） | PASS | `Vehicle BROKERX-L2-0001 takes no new work: CHARGING_POLICY_NOT_APPROVED` | `found` |
| A 的取货单已确认，而 B 在整个窗口里没有任何建单（库里的订单意图、合成 RIoT 的订单） | PASS | `CONFIRMED / BROKERX-L2-0002 / B: 0 intents, 0 RIoT orders` | `CONFIRMED / BROKERX-L2-0002 / B: 0 intents, 0 RIoT orders` |
| 逐车判定、不整机拒绝启动：服务端进程在跑、存活检查 200 | PASS | `200` | `200` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
