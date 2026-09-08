# L2 场景证据：three-vehicle-exit

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T090210371Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `b068eee4ffc0c727cf342d51e149a209ad59bbd2` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002, AGV-L2-003/BROKERX-L2-0003` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T090210371Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 三台车各自拿到一趟 journey | PASS | `AGV-L2-001,AGV-L2-002,AGV-L2-003` | `AGV-L2-001,AGV-L2-002,AGV-L2-003` |
| 三趟 journey 落在三个互不相同的需求上 | PASS | `3` | `3` |
| 三趟 journey 落在三把互不相同的 vehicleKey 上 | PASS | `3` | `3` |
| AGV-L2-001 的 TO_PICKUP 单已建并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| AGV-L2-002 的 TO_PICKUP 单已建并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| AGV-L2-003 的 TO_PICKUP 单已建并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| AGV-L2-001 到站取货、装载提交、出发前安全检查通过，进入关卡段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| AGV-L2-002 到站取货、装载提交、出发前安全检查通过，进入关卡段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| AGV-L2-003 到站取货、装载提交、出发前安全检查通过，进入关卡段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| AGV-L2-001 的装载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-002 的装载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-003 的装载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-001 的 journey 走到 Completed | PASS | `Completed` | `Completed` |
| AGV-L2-002 的 journey 走到 Completed | PASS | `Completed` | `Completed` |
| AGV-L2-003 的 journey 走到 Completed | PASS | `Completed` | `Completed` |
| AGV-L2-001 的卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-002 的卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-003 的卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| 三个需求全部终态 Succeeded | PASS | `3 / 3` | `3 / 3` |
| 全程只建了六张 RIoT 单（三台车各取货一张、关卡一张） | PASS | `6` | `6` |
| 六张单绑在三把 vehicleKey 上，没有一张落到别的车头上 | PASS | `BROKERX-L2-0001,BROKERX-L2-0002,BROKERX-L2-0003` | `BROKERX-L2-0001,BROKERX-L2-0002,BROKERX-L2-0003` |
| 路网快照全程不是陈旧态（引擎在三车链路里真的工作） | PASS | `(无陈旧原因)` | `(null)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
