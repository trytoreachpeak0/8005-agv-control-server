# L2 场景证据：same-direction-mixed-load-multi-drop

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T122651001Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-10` |
| controlServerCommit | `b802d3941fd62e40a95520bfdf84e6cc191b4387` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T122651001Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 三单在同一趟旅程里，任务类型各自保留：受理行的 WorkType 是各自的类型，冻结的卸货站是各自类型绑定的站；库里只有一条旅程 | PASS | `A:WIRE_TO_GATE->210 B:WIRE_TO_OPTICAL->13 C:WIRE_TO_NITROGEN->305 / 1 journey` | `A:WIRE_TO_GATE->210 B:WIRE_TO_OPTICAL->13 C:WIRE_TO_NITROGEN->305 / 1 journey(s)` |
| 本趟两次追加都发生在停站窗口内（不证行驶中拒绝）：乙在车停于 12 号站（甲装完之后、离站订单之前）加入，丙在车停于 11 号站（乙装完之后、离站订单之前）加入；加入那一刻旅程停在站上、当前停靠就是那一站 | PASS | `B at station 12, C at station 11: loaded <= joined < next departure order; AwaitingStationDeparture` | `B: A loaded 12:27:38.684 <= joined 12:27:40.571 < next departure order 12:27:50.574; stage AwaitingStationDeparture, current stop journey:98752af8-f648-4b02-a3a4-ecae12b39421\|PICKUP@12 \| C: B loaded 12:27:52.521 <= joined 12:27:53.706 < next departure order 12:28:03.552; stage AwaitingStationDeparture, current stop journey:3147c104-03d3-42c8-949c-bb22bcc5def3\|PICKUP@11` |
| 计划里有三个不同的卸货停靠，站号各不相同，各是对应那一单的卸货停靠、落在该单类型绑定的站 | PASS | `3 unload stops / A->210 B->13 C->305` | `3 unload stops (1:PICKUP@12 2:PICKUP@11 3:PICKUP@12 4:UNLOAD@305 5:UNLOAD@13 6:UNLOAD@210) / A->210 B->13 C->305` |
| 三单装进不同仓位，各在自己 AREA 指派的那一组（甲 N1-3、丙 N1-7 指 FRONT，乙 C15-13 指 REAR） | PASS | `A FRONT, B REAR, C FRONT; three distinct slots` | `A [1] FRONT; B [5] REAR; C [2] FRONT` |
| 卸货站 305（第 1 个卸货停靠）卸的正是 C（WIRE_TO_NITROGEN）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令 | FAIL | `LOAD [2]; UNLOAD [2]; Load op [2]; Unload op [2]; others +0` | `LOAD [1]; UNLOAD [2]; Load op [1]; Unload op [2]; A+0 B+0` |
| 卸货站 13（第 2 个卸货停靠）卸的正是 B（WIRE_TO_OPTICAL）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令 | FAIL | `LOAD [5]; UNLOAD [5]; Load op [5]; Unload op [5]; others +0` | `LOAD [1]; UNLOAD [5]; Load op [1]; Unload op [5]; A+0 C+0` |
| 卸货站 210（第 3 个卸货停靠）卸的正是 A（WIRE_TO_GATE）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令 | PASS | `LOAD [1]; UNLOAD [1]; Load op [1]; Unload op [1]; others +0` | `LOAD [1]; UNLOAD [1]; Load op [1]; Unload op [1]; B+0 C+0` |
| 旅程 Completed，三单的受理行都是 Succeeded | PASS | `Completed / 3 Succeeded` | `Completed / 3 Succeeded` |
| 两次追加记下的每区参数版本都是 setup 写入的那一版，那一版里 MAP-25-WIRE_TO_GATE 的上限是 50000 | PASS | `1,1 / 50000` | `1,1 / 50000` |
| 上限 30000 时，车停在 12 号站载着丁，戊（同乙的形状，要 40000）的追加被拒：积压原因 EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED，没有受理、没进丁的旅程 | PASS | `EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED / not accepted / 0 memberships / D at station` | `EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED / not accepted / 0 memberships / D AwaitingStationDeparture` |
| 丁照常到关卡卸货、结清，它那一趟旅程里只有它一单 | PASS | `Completed / Succeeded / 1 member` | `Completed / Succeeded / 1 member(s)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
