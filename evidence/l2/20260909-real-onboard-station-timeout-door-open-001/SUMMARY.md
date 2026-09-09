# L2 场景证据：real-onboard-station-timeout-door-open

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260909T094805807Z` |
| agvId | `AGV-L2-001` |
| controlServerCommit | `0f6b42458ab55cde4c9123ad344ddc86c3fe5774` |
| onboardHmiCommit | `98df671c846c7c572fd40d38fe16e5e084686603` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260909T094805807Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站后停在等条码录入这一步 | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 站点期限的起点已经种下 | PASS | `有值` | `SublotWaitStartedAt=2026-09-09 09:48:23.8093882+00:00` |
| 8 号仓的锁反馈读数是 0——这就是一扇没关实的门在 IO 上的样子 | PASS | `0` | `0` |
| 服务端的安全投影里出现了 LOCK_NOT_CLOSED——决策 4 的判据来源是这一个字段 | PASS | `含 LOCK_NOT_CLOSED` | `["LOCK_NOT_CLOSED"]` |
| 门开着时会话仍然 Ready——否则运行时停在就绪门上，期限那一段根本不会被执行 | FAIL | `Ready` | `RecoveryRequired / DEPARTURE_SAFETY_NOT_READY` |
| 期限到期挂上 STATION_TIMEOUT_DOOR_NOT_CLOSED 告警 | FAIL | `STATION_TIMEOUT_DOOR_NOT_CLOSED` | `ONBOARD_SESSION_NOT_READY` |
| 停靠没有被关闭，旅程仍停在 AwaitingSublot 而不是 Blocked | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 需求没有被 CANCELLED_BY_STATION_TIMEOUT 终结——门还开着，本站不许结算 | PASS | `Accepted` | `Accepted` |
| 又转了六轮仍然在等，等待没有自己退化成结束 | PASS | `AwaitingSublot / Accepted` | `AwaitingSublot / Accepted` |
| 8 号仓的锁反馈回到 1 | PASS | `1` | `1` |
| 门一闭合，下一轮立刻按当时的真实读数结算本站 | PASS | `Completed / CANCELLED_BY_STATION_TIMEOUT` | `Completed / CANCELLED_BY_STATION_TIMEOUT` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
