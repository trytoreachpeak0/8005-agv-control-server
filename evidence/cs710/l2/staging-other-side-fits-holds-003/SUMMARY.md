# L2 场景证据：staging-other-side-fits-holds

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261014T163125278Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `73ca883c2ac869d5cd3424e2e018a7411da1e751` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261014T163125278Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 甲装完、FRONT 已无空仓、REAR 全空：CARGO_HOLDING_WAIT，满的一侧只有 FRONT，不是 VEHICLE_FULL | PASS | `CARGO_HOLDING_WAIT, ["FRONT"]` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, ["FRONT"]` |
| REAR 装得下的丙追加进了这趟旅程（同一趟、同站新开一个停靠） | PASS | `journey:c0f6c504-6b2f-403f-9033-4cae3fde8cc7` | `journey:c0f6c504-6b2f-403f-9033-4cae3fde8cc7` |
| 丙装进 REAR 组（5～8）；到这时为止的离站核验只开往待送站本身（原地走到丙的停靠），没有一张开往卸货站 | PASS | `LOADED in 5..8, checks only to 派工待送取货` | `LOADED slots [5], checks to [派工待送取货]` |
| 丙装完、没有等着的需求：又转五轮，装货阶段仍是 CARGO_HOLDING_WAIT，没有开往卸货站的离站核验 | PASS | `CARGO_HOLDING_WAIT, checks only to 派工待送取货` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, checks to [派工待送取货]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
