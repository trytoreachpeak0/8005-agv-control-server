# L2 场景证据：mandatory-charge-vehicle-takes-no-transport

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162414406Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162414406Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：最后一次激活的是场景导入的那一版（强制充电线 30、余量 20、每趟估计 0） | PASS | `OK/OK/OK, v2 30/20/0` | `OK/OK/OK, v2 30/20/0` |
| 两车都在强制充电线 30 与余量 20 之间：派车链答 MANDATORY_CHARGE_REQUIRED，整个窗口里没有旅程、建单、用途占有与站点独占 | PASS | `MANDATORY_CHARGE_REQUIRED / 0 journeys; 0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders; 0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders` | `MANDATORY_CHARGE_REQUIRED / 0 journeys; 0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders; 0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders` |
| B 抬到 80 之后需求派给 B；旅程记下场景激活的那一版与 SUFFICIENT | PASS | `BROKERX-L2-0001 / AGV-L2-001 / v2 / SUFFICIENT` | `BROKERX-L2-0001 / AGV-L2-001 / v2 / SUFFICIENT` |
| A 在整段里没有建单、没有用途占有、没有站点独占或人工充电等待：原地不动（名册里的桩此刻分不到） | PASS | `0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders` | `0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
