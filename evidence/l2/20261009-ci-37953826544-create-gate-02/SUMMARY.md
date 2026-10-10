# L2 场景证据：create-gate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T165917288Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T165917288Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 目录处于 Fresh 且有一次完整确认 | PASS | `Fresh + 已确认` | `Fresh + 2026-10-09 16:59:39.0589361+00:00` |
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
