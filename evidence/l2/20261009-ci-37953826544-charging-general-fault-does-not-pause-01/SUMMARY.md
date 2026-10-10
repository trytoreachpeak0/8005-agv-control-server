# L2 场景证据：charging-general-fault-does-not-pause

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161217874Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161217874Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 到桩后单停在 HANG（9），开始充电的动作带的是结果码 1、不是 407802：另等十五秒，没有暂停事件，周期仍在途（EN_ROUTE／ACTIVE），用途仍是 CHARGING，旅程照原样是 ORDER_HANG | PASS | `0 holds \| EN_ROUTE ACTIVE \| CHARGING \| ORDER_HANG / order 9 / act code 1` | `0 holds \| EN_ROUTE ACTIVE \| CHARGING \| ORDER_HANG / order 9 / act code 1` |
| 同一张单改为带 407802 停在 HANG，可车报在关卡 210、不在 211 上（未到桩）：另等十五秒，同样没有暂停事件，周期、用途与 ORDER_HANG 都不变 | PASS | `0 holds \| EN_ROUTE ACTIVE \| CHARGING \| ORDER_HANG / order 9 / act code 407802` | `0 holds \| EN_ROUTE ACTIVE \| CHARGING \| ORDER_HANG / order 9 / act code 407802` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
