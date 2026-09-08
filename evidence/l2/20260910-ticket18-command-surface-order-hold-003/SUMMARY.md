# L2 场景证据：command-surface-order-hold

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T090257232Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T090257232Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 在途单还正常时，命令面一次都没被调用过 | PASS | `0` | `0` |
| 假 RIoT 侧也没收到过任何命令请求 | PASS | `0` | `0` |
| 该调用时调用了：单被报成 FAILED 之后，命令面发出了一条命令 | PASS | `>= 1` | `1` |
| 参数正确：命令是 OrderHold（REQ-0234 明令不得用 Cancel） | PASS | `OrderHold` | `OrderHold` |
| 参数正确：命令记在 AGV-L2-002 名下 | PASS | `AGV-L2-002` | `AGV-L2-002` |
| 参数正确：目标是这一趟自己的 upperId（审计的键） | PASS | `W2G-6ef518ec-f6f0-400e-96dd-274a8103fb13-PICKUP-1` | `W2G-6ef518ec-f6f0-400e-96dd-274a8103fb13-PICKUP-1` |
| 参数正确：目标带着 RIoT 自己的 orderId（端点的地址），两个身份都在 | PASS | `ORDER-000002` | `ORDER-000002` |
| 参数正确：线上收到的是 CMD_ORDER_HELD，打在这一张 orderId 上 | PASS | `CMD_ORDER_HELD @ ORDER-000002` | `1 条，首条打在 ORDER-000002` |
| 只调一次：又过了 8 轮评估，审计表仍然只有一行 attempt | PASS | `1` | `1` |
| 只调一次：那一行的 AttemptNumber 是 1（重试会是新的一行，不是覆盖） | PASS | `1` | `1` |
| 只调一次：RIoT 侧也只收到过一条 CMD_ORDER_HELD，两边计数一致 | PASS | `1` | `1` |
| 停在站上的车没有被升级成急停（REQ-0246 的第三个条件不成立） | PASS | `0` | `0` |
| 只有 AGV-L2-002 有故障事实，另外两台一条都没有 | PASS | `1 行 / AGV-L2-002` | `1 行 / AGV-L2-002` |
| 故障停在第一级 SuspectedBlocked：一个症状不越级成隔离（REQ-0232） | PASS | `SuspectedBlocked` | `SuspectedBlocked` |
| 证据码是 VEHICLE_ORDER_FAILED，能追到具体是哪个症状 | PASS | `VEHICLE_ORDER_FAILED` | `VEHICLE_ORDER_FAILED` |
| AGV-L2-002 的 journey 把停住的原因写成 VEHICLE_ORDER_FAILED，而不是留空 | PASS | `VEHICLE_ORDER_FAILED` | `VEHICLE_ORDER_FAILED` |
| AGV-L2-001 不受牵连：仍在自己的到站阶段，没有阻断原因 | PASS | `AwaitingPickupArrival / (无阻断原因)` | `AwaitingPickupArrival / (无)` |
| AGV-L2-003 不受牵连：仍在自己的到站阶段，没有阻断原因 | PASS | `AwaitingPickupArrival / (无阻断原因)` | `AwaitingPickupArrival / (无)` |
| AGV-L2-001 在另一台车带着故障事实的同时照常取货装载 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| 全程命令面只有一行 attempt，旁观车的推进没有带出任何额外命令 | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
