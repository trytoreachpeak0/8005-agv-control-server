# L2 场景证据：same-direction-wire-to-nitrogen-journey

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T183609803Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `6b71be5175eb90151ae01dd050658ee99563fe7e` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `13eca82ac199bd97c1ad49d036c70cb5b012250f` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `128daae52758749d4d6f346231fdb336ca5d1079773bcab5430c20c53ce6b10b` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T183609803Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：本类固定站 404「氮气柜」在地图上、站名不是 AREA 格式；归属表 N1-3 → REAR、N1-7 → FRONT（同挂机台站 12） | PASS | `404=氮气柜 / N1-3/REAR,N1-7/FRONT` | `404=氮气柜 / N1-3/REAR,N1-7/FRONT` |
| WIRE_TO_NITROGEN 需求（N1-3）被受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是 WIRE_TO_NITROGEN | PASS | `AwaitingPickupArrival / WIRE_TO_NITROGEN` | `AwaitingPickupArrival / WIRE_TO_NITROGEN / backlog reason ACCEPTED` |
| N1-3 指 REAR：取货扫码后分到的目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |
| WIRE_TO_NITROGEN（N1-3）两端没有对调：取货在 AREA 机台站 12、卸货在本类绑定站 404（不是关卡 210）；冻结的卸货站与两条腿的订单终点一致 | PASS | `pickup 12 / drop-off 404 / frozen 404 / legs 12 -> 404` | `pickup 12 / drop-off 404 / frozen 404 / legs 12 -> 404` |
| WIRE_TO_NITROGEN（N1-3）：机台站下发的 LOAD 与绑定站下发的 UNLOAD，slots 都等于目标仓 | PASS | `LOAD [5,6]; UNLOAD [5,6]` | `LOAD [5,6]; UNLOAD [5,6]` |
| WIRE_TO_NITROGEN（N1-3）卸货完成、需求结清：旅程 Completed，卸货操作 Committed，受理行 Succeeded | PASS | `Completed / 1 committed unload / Succeeded` | `Completed / 1 committed unload(s) / Succeeded` |
| WIRE_TO_NITROGEN 需求（N1-7）被受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是 WIRE_TO_NITROGEN | PASS | `AwaitingPickupArrival / WIRE_TO_NITROGEN` | `AwaitingPickupArrival / WIRE_TO_NITROGEN / backlog reason ACCEPTED` |
| N1-7 指 FRONT：取货扫码后分到的目标仓全部属于本车 FRONT 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[1,2]（FRONT 组最小的可用仓，升序）` | `[1,2] on AGV-L2-001` |
| WIRE_TO_NITROGEN（N1-7）两端没有对调：取货在 AREA 机台站 12、卸货在本类绑定站 404（不是关卡 210）；冻结的卸货站与两条腿的订单终点一致 | PASS | `pickup 12 / drop-off 404 / frozen 404 / legs 12 -> 404` | `pickup 12 / drop-off 404 / frozen 404 / legs 12 -> 404` |
| WIRE_TO_NITROGEN（N1-7）：机台站下发的 LOAD 与绑定站下发的 UNLOAD，slots 都等于目标仓 | PASS | `LOAD [1,2]; UNLOAD [1,2]` | `LOAD [1,2]; UNLOAD [1,2]` |
| WIRE_TO_NITROGEN（N1-7）卸货完成、需求结清：旅程 Completed，卸货操作 Committed，受理行 Succeeded | PASS | `Completed / 1 committed unload / Succeeded` | `Completed / 1 committed unload(s) / Succeeded` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
