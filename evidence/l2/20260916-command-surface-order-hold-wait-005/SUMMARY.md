# L2 场景证据：command-surface-order-hold

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260916T155149584Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `7eda7b4a8918950166cf984c973f1fc4d242b06e` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002, AGV-L2-003/BROKERX-L2-0003` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260916T155149584Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 在途单还正常时，命令面一次都没被调用过 | PASS | `0` | `0` |
| 假 RIoT 侧也没收到过任何命令请求 | PASS | `0` | `0` |
| 该调用时调用了：单被报成 FAILED 之后，命令面发出了一条命令 | PASS | `>= 1` | `1` |
| 参数正确：命令是 OrderHold（REQ-0234 明令不得用 Cancel） | PASS | `OrderHold` | `OrderHold` |
| 参数正确：命令记在 AGV-L2-002 名下 | PASS | `AGV-L2-002` | `AGV-L2-002` |
| 参数正确：目标是这一趟自己的 upperId（审计的键） | PASS | `W2G-1d0628a0-0a0d-4614-9a1f-6178c1bbdd34-PICKUP-1` | `W2G-1d0628a0-0a0d-4614-9a1f-6178c1bbdd34-PICKUP-1` |
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
