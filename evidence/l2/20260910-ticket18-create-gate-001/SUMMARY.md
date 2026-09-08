# L2 场景证据：create-gate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260908T090111002Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260908T090111002Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 目录处于 Fresh 且有一次完整确认 | PASS | `Fresh + 已确认` | `Fresh + 2026-09-08 09:01:20.459134+00:00` |
| 判定所依据的两个已批准值被一并记下（30 秒周期 / 300 秒最大未确认） | PASS | `30 / 300` | `30 / 300` |
| 门禁在链路里放行，派车照常走通 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 取货端与卸货端两个端点都被冻结 | PASS | `2` | `2` |
| 冻结的端点带着当时的目录修订（不是 0） | PASS | `非 0` | `1017238931678627222` |
| 卸货端冻的是关卡站 | PASS | `210` | `210` |
| 门禁写了一条 Allowed 审计（同一裁决不重复写，所以只有一条） | PASS | `1 条 Allowed` | `1 条 / Allowed` |
| RIoT 的 RouteCost 与自建图的遍历代价分列两栏，都有值 | PASS | `两栏都有值` | `RIoT=20000 / Graph=60000` |
| 两个源都说可达，没有分歧要处置 | PASS | `可达 + 可达 + 无分歧` | `RIoT=20000 / GraphReachable=1 / Conflict=` |
| 目录级状态经正式 API 读得回来，一张图一行（按原因去重，不是每轮一条） | PASS | `1 行 Fresh` | `1 行 / Fresh` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
