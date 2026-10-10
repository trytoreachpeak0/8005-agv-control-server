# L2 场景证据：same-direction-die-to-oven-journey

结论：**FAIL**

失败原因：L2-SDJ-DOV-REAR-01 did not hold: the DIE_TO_OVEN demand (N1-3) was not accepted (backlog reason 'TASK_TYPE_NOT_YET_EXECUTABLE'); every later criterion needs its journey.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T105445082Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-10` |
| controlServerCommit | `8b780a24d92e7b141b1c0106a706da1171a13281` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T105445082Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：本类固定站 402「烘箱」在地图上、站名不是 AREA 格式；归属表 N1-3 → REAR、N1-7 → FRONT（同挂机台站 12） | PASS | `402=烘箱 / N1-3/REAR,N1-7/FRONT` | `402=烘箱 / N1-3/REAR,N1-7/FRONT` |
| DIE_TO_OVEN 需求（N1-3）被受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是 DIE_TO_OVEN | FAIL | `AwaitingPickupArrival / DIE_TO_OVEN` | `(no journey) / (not accepted) / backlog reason TASK_TYPE_NOT_YET_EXECUTABLE` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
