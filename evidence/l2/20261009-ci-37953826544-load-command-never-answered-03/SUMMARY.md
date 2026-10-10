# L2 场景证据：load-command-never-answered

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163403645Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163403645Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载指令已下发，旅程在等结果 | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 旅程仍停在 AwaitingLoadResult（没有进 Blocked） | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 没有阻塞原因码（Blocked 与「没有结果」是两种状态） | PASS | `(none)` | `(none)` |
| 装载操作停在 Prepared（不是 RecoveryRequired，也不是 Committed） | PASS | `Prepared` | `Prepared` |
| 需求仍是 Accepted（既没成功，也没转恢复） | PASS | `Accepted` | `Accepted` |
| 未结的装载指令在被持续重放（不是发一次就忘） | PASS | `> 1` | `5` |
| 重放用的是同一条命令身份（对端只有一条待答请求） | PASS | `operation:bff79f97-0049-375d-b763-65d8249d0b43` | `operation:bff79f97-0049-375d-b763-65d8249d0b43` |
| 每次重放都是同一个 messageId（不是每轮新造一条命令） | PASS | `1` | `1` |
| 没有装载结果就不建 TO_GATE 单 | PASS | `(none)` | `(none)` |
| 没有为关卡段派过车（RIoT 单仍只有一条） | PASS | `1` | `1` |
| 没有下发出发前安全检查 | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
