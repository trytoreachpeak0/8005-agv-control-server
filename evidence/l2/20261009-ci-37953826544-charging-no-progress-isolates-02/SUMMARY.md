# L2 场景证据：charging-no-progress-isolates

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161336860Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161336860Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 观察窗口起点开窗之后不被新样本重置：形成时周期上的起点仍是开窗那一刻（2026-10-09 16:14:09.4981652+00:00），形成时刻距起点不少于窗口 20 秒 | PASS | `window start 2026-10-09 16:14:09.4981652+00:00 / >= 20 s` | `window start 2026-10-09 16:14:09.4981652+00:00 / 21 s` |
| 一直报 CHARGING、电量不涨：窗口满后桩的分配暂停与车的充电资格暂停都写下（NO_PROGRESS_CONFIRMED，根因 UNKNOWN），周期进清桩中，用途转 CLEARING_MAINTENANCE，211 仍是这一趟的占用 | PASS | `NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T161402053Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161402053Z \| journey AwaitingPickupArrival CHARGING_NO_PROGRESS_CONFIRMED` | `NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261009T161402053Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161402053Z \| journey AwaitingPickupArrival CHARGING_NO_PROGRESS_CONFIRMED` |
| 形成之后另等十秒：RIoT 上一直只有那一张充电单，命令审计 0 条，211 一直是这一趟的占用，两侧各只有一条暂停 | PASS | `W2G-CHARGE-BROKERX-L2-0001-20261009T161402053Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161402053Z \| NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED` | `W2G-CHARGE-BROKERX-L2-0001-20261009T161402053Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261009T161402053Z \| NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
