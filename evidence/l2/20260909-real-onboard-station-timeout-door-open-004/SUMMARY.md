# L2 场景证据：real-onboard-station-timeout-door-open

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260909T130648457Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `edbba269028e8fc3d58a09a077dab863d1b5c3e9` |
| onboardHmiCommit | `3d8206fc9fc4ce02b58e78a205d9563be68c6b0e` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260909T130648457Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站后停在等条码录入这一步 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 站点期限的起点已经种下——它是决策 4 那一格的计时起点 | PASS | `有值` | `SublotWaitStartedAt=2026-09-09 13:07:07.527703+00:00` |
| 扫码之后旅程进到 AwaitingLoadResult——决策 4 的告警只属于这一格 | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 1 号仓门确实弹开了、开锁输出已复位——这就是操作员走开时的现场原样 | PASS | `OPEN/EMPTY/0/0` | `OPEN/EMPTY/0/0` |
| 服务端的安全投影里出现了 LOCK_NOT_CLOSED——决策 4 的判据来源是这一个字段 | PASS | `含 LOCK_NOT_CLOSED` | `["LOCK_NOT_CLOSED"]` |
| 门开着而会话仍然 Ready——这扇门由本服务端自己下的命令解释，不是会话故障 | PASS | `Ready` | `Ready / READY` |
| 期限到期挂上 STATION_TIMEOUT_DOOR_NOT_CLOSED 告警 | PASS | `STATION_TIMEOUT_DOOR_NOT_CLOSED` | `STATION_TIMEOUT_DOOR_NOT_CLOSED` |
| 停靠没有被关闭，旅程仍停在 AwaitingLoadResult 而不是 Blocked | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 需求没有被 CANCELLED_BY_STATION_TIMEOUT 终结——门还开着，本站不许结算 | PASS | `Accepted` | `Accepted` |
| 期限到期没有把在途装载判进人工恢复 | PASS | `Prepared` | `Prepared` |
| 又转了六轮仍然在等，等待没有自己退化成结束 | PASS | `AwaitingLoadResult / Accepted` | `AwaitingLoadResult / Accepted` |
| 第 1 次关门：1 号仓关到位、锁反馈回到 1、货位仍是空——明确的相反态，不是 UNKNOWN | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 期限过了之后第一次关门换来的是再开一次门，不是判死——决策 1 先赢一轮 | PASS | `UNLOCKING > 1 / OPEN/EMPTY/0/0` | `2 / OPEN/EMPTY/0/0` |
| 门重新弹开之后告警还在——它名的是一个条件，不是一个事件 | PASS | `STATION_TIMEOUT_DOOR_NOT_CLOSED` | `STATION_TIMEOUT_DOOR_NOT_CLOSED` |
| 第 2 次关门：1 号仓关到位、锁反馈回到 1、货位仍是空——明确的相反态，不是 UNKNOWN | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| 第二次相反态按决策 5 结算成确定失败，而不是 RecoveryRequired | PASS | `Failed` | `Failed` |
| 确定失败落地之后告警撤销，停靠不再顶着一条已经不成立的告警 | PASS | `不是 STATION_TIMEOUT_DOOR_NOT_CLOSED` | `(none)` |
| 全程会话没有离开 Ready——没有任何一步把「人没放料」当成传感器故障 | PASS | `Ready` | `Ready / READY` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
