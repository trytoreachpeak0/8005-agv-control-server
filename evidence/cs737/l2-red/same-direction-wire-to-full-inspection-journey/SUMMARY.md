# L2 场景证据：same-direction-wire-to-full-inspection-journey

结论：**FAIL**

失败原因：L2-SDJ-WFI-REAR-01 did not hold: the WIRE_TO_FULL_INSPECTION demand (N1-3) was not accepted (backlog reason 'TASK_TYPE_NOT_YET_EXECUTABLE'); every later criterion needs its journey.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T152515864Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3d439fbb080f5af25e07b4fbf0e27f502b577627` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `9d7915786b736a6fa8d0c8737bfb115ac08a30b3` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `743b886d99194038b893e98aed2a1dd01409cd7f80d39c734e02d860e0e5827c` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T152515864Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：本类固定站 405「全检区」在地图上、站名不是 AREA 格式；归属表 N1-3 → REAR、N1-7 → FRONT（同挂机台站 12） | PASS | `405=全检区 / N1-3/REAR,N1-7/FRONT` | `405=全检区 / N1-3/REAR,N1-7/FRONT` |
| WIRE_TO_FULL_INSPECTION 需求（N1-3）被受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是 WIRE_TO_FULL_INSPECTION | FAIL | `AwaitingPickupArrival / WIRE_TO_FULL_INSPECTION` | `(no journey) / (not accepted) / backlog reason TASK_TYPE_NOT_YET_EXECUTABLE` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
