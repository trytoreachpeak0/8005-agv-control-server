# L2 场景证据：same-direction-four-type-mixed-load-multi-drop

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T153126415Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T153126415Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四单在同一趟旅程里，任务类型各自保留：受理行的 WorkType 是各自的类型，冻结的卸货站是各自类型绑定的站；库里只有一条旅程 | PASS | `A:WIRE_TO_GATE->210 B:WIRE_TO_OPTICAL->13 C:WIRE_TO_NITROGEN->305 D:WIRE_TO_FULL_INSPECTION->406 / 1 journey` | `A:WIRE_TO_GATE->210 B:WIRE_TO_OPTICAL->13 C:WIRE_TO_NITROGEN->305 D:WIRE_TO_FULL_INSPECTION->406 / 1 journey(s)` |
| 本趟三次追加都发生在停站窗口内（不证行驶中拒绝）：乙在车停于 12 号站、丙在车停于 11 号站、丁在车再停于 12 号站时加入，各自晚于前一单装完、早于离站订单；加入那一刻旅程停在站上、当前停靠就是那一站 | PASS | `B at station 12, C at station 11, D at station 12: loaded <= joined < next departure order; AwaitingStationDeparture` | `B: A loaded 15:31:47.898 <= joined 15:31:49.773 < next departure order 15:31:59.796; stage AwaitingStationDeparture, current stop journey:852efbfb-3347-4e48-9c45-c327bbdf7563\|PICKUP@12 \| C: B loaded 15:32:01.747 <= joined 15:32:03.074 < next departure order 15:32:13.900; stage AwaitingStationDeparture, current stop journey:5575a1a5-de97-486a-907a-37139016d19a\|PICKUP@11 \| D: C loaded 15:32:15.736 <= joined 15:32:17.753 < next departure order 15:32:27.917; stage AwaitingStationDeparture, current stop journey:04b96c7b-aa4d-4188-8834-034476379b1d\|PICKUP@12` |
| 计划里有四个不同的卸货停靠，站号各不相同，各是对应那一单的卸货停靠、落在该单类型绑定的站；丁的取货是 12 号站上排在丙之后的另一个停靠 | PASS | `4 unload stops / A->210 B->13 C->305 D->406 / D picks up at its own stop on 12 after C` | `4 unload stops (1:PICKUP@12 2:PICKUP@11 3:PICKUP@12 4:PICKUP@12 5:UNLOAD@305 6:UNLOAD@13 7:UNLOAD@210 8:UNLOAD@406) / A->210 B->13 C->305 D->406 / D after C: True` |
| 四单装进不同仓位，各在自己 AREA 指派的那一组（甲 N1-3、丙 N1-7、丁 N2-5 指 FRONT，乙 C15-13 指 REAR） | PASS | `A FRONT, B REAR, C FRONT, D FRONT; four distinct slots` | `A [1] FRONT; B [5] REAR; C [2] FRONT; D [3] FRONT` |
| 卸货站 305（第 1 个卸货停靠）卸的正是 C（WIRE_TO_NITROGEN）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令 | PASS | `LOAD [2]; UNLOAD [2]; Load op [2]; Unload op [2]; others +0` | `LOAD [2]; UNLOAD [2]; Load op [2]; Unload op [2]; A+0 B+0 D+0` |
| 卸货站 13（第 2 个卸货停靠）卸的正是 B（WIRE_TO_OPTICAL）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令 | PASS | `LOAD [5]; UNLOAD [5]; Load op [5]; Unload op [5]; others +0` | `LOAD [5]; UNLOAD [5]; Load op [5]; Unload op [5]; A+0 C+0 D+0` |
| 卸货站 210（第 3 个卸货停靠）卸的正是 A（WIRE_TO_GATE）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令 | PASS | `LOAD [1]; UNLOAD [1]; Load op [1]; Unload op [1]; others +0` | `LOAD [1]; UNLOAD [1]; Load op [1]; Unload op [1]; B+0 C+0 D+0` |
| 卸货站 406（第 4 个卸货停靠）卸的正是 D（WIRE_TO_FULL_INSPECTION）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令 | PASS | `LOAD [3]; UNLOAD [3]; Load op [3]; Unload op [3]; others +0` | `LOAD [3]; UNLOAD [3]; Load op [3]; Unload op [3]; A+0 B+0 C+0` |
| 旅程 Completed，四单的受理行都是 Succeeded | PASS | `Completed / 4 Succeeded` | `Completed / 4 Succeeded` |
| 三次追加记下的每区参数版本都是 setup 写入的那一版，那一版里 MAP-25-WIRE_TO_GATE 的上限是 50000 | PASS | `1,1,1 / 50000` | `1,1,1 / 50000` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
