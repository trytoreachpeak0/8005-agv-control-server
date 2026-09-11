# L2 场景证据：real-onboard-multi-demand-operator-inaction

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T073758066Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `55bfa9b4098f6f2d29db9f42bf32792b036f8679` |
| onboardHmiCommit | `bb58b217e95c611de597212d763f7579fdcb0e03` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T073758066Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四条需求凑成一趟旅程，四个取货停靠落在四个不同站点上 | PASS | `4 PICKUP / 4 stations` | `4 PICKUP / 4 stations` |
| 停靠 1：车载端说在等操作员时，它开的那个仓门确实开着、货位是空的 | PASS | `OPEN/EMPTY/0/0` | `OPEN/EMPTY/0/0` |
| 停靠 1 第 1 轮：关门不放料是明确的相反态，车载端自动重开并再次等操作员，没有判失败 | PASS | `CLOSED/EMPTY/1/0 -> UNLOCKING+1 -> OPEN / WAITING_OPERATOR+1` | `CLOSED/EMPTY/1/0 -> UNLOCKING 1->2 -> OPEN / WAITING_OPERATOR 1->2` |
| 停靠 1 第 2 轮：关门不放料是明确的相反态，车载端自动重开并再次等操作员，没有判失败 | PASS | `CLOSED/EMPTY/1/0 -> UNLOCKING+1 -> OPEN / WAITING_OPERATOR+1` | `CLOSED/EMPTY/1/0 -> UNLOCKING 2->3 -> OPEN / WAITING_OPERATOR 2->3` |
| 停靠 1：重开两轮之后照常放料提交，没有进恢复 | PASS | `CLOSED/OCCUPIED/1/0 / Committed` | `CLOSED/OCCUPIED/1/0 / Committed` |
| 停靠 2：车载端为这次装载开了门，操作员走开——这就是现场原样，不需要注入 | PASS | `OPEN/EMPTY/0/0` | `OPEN/EMPTY/0/0` |
| 停靠 2：期限到期挂告警而不结束本站——旅程仍在 AwaitingLoadResult，装载仍在进行（决策 4） | PASS | `STATION_TIMEOUT_DOOR_NOT_CLOSED / 2/AwaitingLoadResult / Prepared` | `STATION_TIMEOUT_DOOR_NOT_CLOSED / 2/AwaitingLoadResult / Prepared` |
| 停靠 2：门一直开着，提示节拍到期只再提示一次、不重复脉冲（决策 3） | PASS | `WAITING_OPERATOR > 1 / UNLOCKING = 1` | `WAITING_OPERATOR 2 / UNLOCKING 1` |
| 停靠 2：期限过后第一次关门不放料换来的是再开一次门，不是判死 | PASS | `UNLOCKING+1 / OPEN` | `UNLOCKING 1->2 / OPEN` |
| 停靠 2：第二次相反态结算成确定失败而不是 RecoveryRequired，会话仍 Ready（决策 5） | PASS | `Failed / Ready` | `Failed / Ready` |
| 停靠 2：确定失败落地之后告警撤销 | PASS | `(not STATION_TIMEOUT_DOOR_NOT_CLOSED)` | `(none)` |
| 停靠 2：确定失败之后旅程自己离开这一站，不需要任何人按任何按钮 | PASS | `(not stop 2)` | `3/AwaitingPickupArrival` |
| 停靠 2：这条需求以 CANCELLED_BY_STATION_TIMEOUT 终结并被永久抑制，它的装货命令已结算、不会被重放进后续会话 | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled` |
| 停靠 3：前两站之后照常装载提交 | PASS | `CLOSED/OCCUPIED/1/0 / Committed` | `CLOSED/OCCUPIED/1/0 / Committed` |
| 停靠 4：前两站之后照常装载提交 | PASS | `CLOSED/OCCUPIED/1/0 / Committed` | `CLOSED/OCCUPIED/1/0 / Committed` |
| 三站装完、一站取消之后旅程进入去关卡那一段，并记下装货收尾的理由 | PASS | `AwaitingGateArrival / a reason` | `AwaitingGateArrival / NO_FURTHER_CARGO` |
| 全程没有任何一次装载被判 RecoveryRequired | PASS | `0` | `0` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 116 lines, 116 distinct, 11 message types, 0 distinct violations, 0 known; schema compilation 20110 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
