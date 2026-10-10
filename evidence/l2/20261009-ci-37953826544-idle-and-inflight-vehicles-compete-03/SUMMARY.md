# L2 场景证据：idle-and-inflight-vehicles-compete

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161948387Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161948387Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 第一条需求让车队里的一台车进入在途状态，另一台仍空闲 | PASS | `AGV-L2-001 或 AGV-L2-002` | `AGV-L2-002` |
| 三条需求都进了某一趟旅程：没有一条被留在积压里等下一轮 | PASS | `3` | `3` |
| 两台车各带一趟旅程：两条路在同一轮里各走了一次 | PASS | `2` | `2` |
| 在途车那一趟带了两条需求：追加这条路走通了 | PASS | `2` | `2` |
| 另一台车那一趟只带一条需求：新建这条路同一轮里也走了一次 | PASS | `1` | `1` |
| 在途车的计划是三个停靠，序位 1 仍是它原本就要去的那一站（REQ-0196） | PASS | `3 个停靠，1:PICKUP 在前` | `3 个停靠：1:PICKUP@12 2:PICKUP@12 3:UNLOAD@210` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
