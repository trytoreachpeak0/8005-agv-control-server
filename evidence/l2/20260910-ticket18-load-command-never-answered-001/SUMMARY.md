# L2 场景证据：load-command-never-answered

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T090042722Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `b068eee4ffc0c727cf342d51e149a209ad59bbd2` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `0.1.1` |
| protocolReleaseIdentity.tag | `protocol-v0.1.1` |
| protocolReleaseIdentity.commit | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279` |
| protocolReleaseIdentity.protocolVersion | `1` |
| protocolReleaseIdentity.profileId | `WIRE_TO_GATE_MVP` |
| protocolReleaseIdentity.manifestSha256 | `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f` |
| protocolReleaseIdentity.schemaBundleSha256 | `e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c` |
| protocolReleaseIdentity.vectorsSha256 | `fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T090042722Z` |
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
| 重放用的是同一条命令身份（对端只有一条待答请求） | PASS | `operation:f7154c60-99d7-665c-841a-7006443e326a` | `operation:f7154c60-99d7-665c-841a-7006443e326a` |
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
