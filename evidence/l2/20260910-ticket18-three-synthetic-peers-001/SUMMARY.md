# L2 场景证据：three-synthetic-peers

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T090138449Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T090138449Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 编排器按 setup 起了三个合成车载端 | PASS | `3` | `3` |
| 三个控制面各占一个端口 | PASS | `3` | `3` |
| 三个车载端各报各的 AgvId | PASS | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` |
| AGV-FAKE-001 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-001` | `AGV-FAKE-001` |
| AGV-FAKE-002 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-002` | `AGV-FAKE-002` |
| AGV-FAKE-003 的控制面在线并报出自己的身份 | PASS | `AGV-FAKE-003` | `AGV-FAKE-003` |
| 三个控制面报出三个互不相同的身份 | PASS | `3` | `3` |
| 服务端同时持有三台车的会话，一台一行 | PASS | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` | `AGV-FAKE-001,AGV-FAKE-002,AGV-FAKE-003` |
| AGV-FAKE-001 的会话在服务端是 Ready | PASS | `Ready` | `Ready` |
| AGV-FAKE-002 的会话在服务端是 Ready | PASS | `Ready` | `Ready` |
| AGV-FAKE-003 的会话在服务端是 Ready | PASS | `Ready` | `Ready` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
