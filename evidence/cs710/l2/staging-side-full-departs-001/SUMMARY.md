# L2 场景证据：staging-side-full-departs

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261014T152425795Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d4d49930aa39a23e996bff639f5a70080a650d43` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261014T152425795Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 甲装完、FRONT 已无空仓、REAR 全空，没有等着的需求：CARGO_HOLDING_WAIT，满的一侧只有 FRONT | PASS | `CARGO_HOLDING_WAIT, ["FRONT"]` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, ["FRONT"]` |
| 又转了五轮，仍没有等着的需求：车不自己走（不发离站核验），装货阶段仍是 CARGO_HOLDING_WAIT | PASS | `CARGO_HOLDING_WAIT, 0 checks` | `AwaitingStationDeparture CARGO_HOLDING_WAIT, 0 checks` |
| 派车轮判乙 SLOT_GROUP_OCCUPIED_BY_OWN_CARGO（FRONT 只因本车的货装不下） | PASS | `SLOT_GROUP_OCCUPIED_BY_OWN_CARGO` | `SLOT_GROUP_OCCUPIED_BY_OWN_CARGO` |
| 等着的需求全要 FRONT 且装不下：装货阶段 VEHICLE_FULL（REAR 还空着） | PASS | `VEHICLE_FULL` | `AwaitingDepartureSafety VEHICLE_FULL, ["FRONT"]` |
| 判满之后立即发出开往卸货站的离站核验：乙发布后一分钟之内，远早于十分钟的离站期限与持货期限 | PASS | `check < 60 s after B` | `check to N1-3_N1-7 1.8 s after B` |
| 乙没有进这趟旅程 | PASS | `no journey` | `no journey` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
