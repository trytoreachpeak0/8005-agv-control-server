# L2 场景证据：same-direction-die-to-oven-journey

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T104543872Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-10` |
| controlServerCommit | `588fc34c2b12acb5560a7d260cfc53949ac81459` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T104543872Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：本类固定站 402「烘箱」在地图上、站名不是 AREA 格式；归属表 N1-3 → REAR、N1-7 → FRONT（同挂机台站 12） | PASS | `402=烘箱 / N1-3/REAR,N1-7/FRONT` | `402=烘箱 / N1-3/REAR,N1-7/FRONT` |
| DIE_TO_OVEN 需求（N1-3）被受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是 DIE_TO_OVEN | PASS | `AwaitingPickupArrival / DIE_TO_OVEN` | `AwaitingPickupArrival / DIE_TO_OVEN / backlog reason ACCEPTED` |
| N1-3 指 REAR：取货时目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |
| DIE_TO_OVEN（N1-3）两端没有对调：取货在 AREA 机台站 12、卸货在本类绑定站 402（不是关卡 210）；冻结的卸货站与两条腿的订单终点一致 | PASS | `pickup 12 / drop-off 402 / frozen 402 / legs 12 -> 402` | `pickup 12 / drop-off 402 / frozen 402 / legs 12 -> 402` |
| DIE_TO_OVEN（N1-3）：机台站下发的 LOAD 与绑定站下发的 UNLOAD，slots 都等于目标仓 | PASS | `LOAD [5,6]; UNLOAD [5,6]` | `LOAD [5,6]; UNLOAD [5,6]` |
| DIE_TO_OVEN（N1-3）卸货完成、需求结清：旅程 Completed，卸货操作 Committed，受理行 Succeeded | PASS | `Completed / 1 committed unload / Succeeded` | `Completed / 1 committed unload(s) / Succeeded` |
| DIE_TO_OVEN 需求（N1-7）被受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是 DIE_TO_OVEN | PASS | `AwaitingPickupArrival / DIE_TO_OVEN` | `AwaitingPickupArrival / DIE_TO_OVEN / backlog reason ACCEPTED` |
| N1-7 指 FRONT：取货时目标仓全部属于本车 FRONT 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[1,2]（FRONT 组最小的可用仓，升序）` | `[1,2] on AGV-L2-001` |
| DIE_TO_OVEN（N1-7）两端没有对调：取货在 AREA 机台站 12、卸货在本类绑定站 402（不是关卡 210）；冻结的卸货站与两条腿的订单终点一致 | PASS | `pickup 12 / drop-off 402 / frozen 402 / legs 12 -> 402` | `pickup 12 / drop-off 402 / frozen 402 / legs 12 -> 402` |
| DIE_TO_OVEN（N1-7）：机台站下发的 LOAD 与绑定站下发的 UNLOAD，slots 都等于目标仓 | PASS | `LOAD [1,2]; UNLOAD [1,2]` | `LOAD [1,2]; UNLOAD [1,2]` |
| DIE_TO_OVEN（N1-7）卸货完成、需求结清：旅程 Completed，卸货操作 Committed，受理行 Succeeded | PASS | `Completed / 1 committed unload / Succeeded` | `Completed / 1 committed unload(s) / Succeeded` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
