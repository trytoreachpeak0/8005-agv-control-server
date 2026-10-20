# control-server#823 证据

L2 场景 `charging-no-progress-isolates` 的判据 L2-CNP-02 误红（CI run 39481673510，原文在 `evidence/cs820/l2-red/`）。
四轮都在本机（合成车载端，`Invoke-HeavyLocal.ps1` 下，`-PortSlot 5`）。时间为 UTC，2026-10-20；服务端日志里的时刻是本机时间（UTC+8）。

| 目录 | 脚本 | 注入 | 结论 | L2-CNP-02 读到的旅程原因 |
| --- | --- | --- | --- | --- |
| `red-1/` | 改前（`1b1fa80d9`） | 两次读之间 `Start-Sleep -Seconds 6` | FAIL | `CHARGING_CLEARING_VEHICLE_STILL_CHARGING` |
| `red-2/` | 改前（`HEAD~1` 的场景文件） | 同上，`injections/red-before-fix.ps1` | FAIL | `CHARGING_CLEARING_VEHICLE_STILL_CHARGING` |
| `green-1/` | 改后（`83ca7e202`） | 无 | PASS | `CHARGING_NO_PROGRESS_CONFIRMED` |
| `green-delay-1/` | 改后（`83ca7e202`） | 探针里读周期与读旅程之间 `Start-Sleep -Seconds 6`，`injections/delay-after-fix.ps1` | PASS | `CHARGING_CLEARING_VEHICLE_STILL_CHARGING` |

每轮只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl` 和服务端日志里两行（形成隔离、清桩中车仍在充电）的摘录。

## 读法

- 两轮红与 CI 那次的期望、实际逐字同形：只差最后一段旅程原因。注入是一行等待，场景其余不变；`SUMMARY.md` 里的
  `controlServerCommit` 是当时的 `HEAD`，注入行不在提交里。
- 原因推进的时刻：形成隔离的那一轮写 `CHARGING_NO_PROGRESS_CONFIRMED`，下一轮的清桩等待读到假 RIoT 仍报 CHARGING，改写成
  `CHARGING_CLEARING_VEHICLE_STILL_CHARGING`（`ClearingWaitCodeAsync`）。四轮的日志摘录里两行相隔 0～1 秒；`green-1/` 两行在同一秒，
  也就是说即使把读取合到一次，第一次看到清桩中时原因也可能已经推进。所以改法是两件事一起：同一次探针里先读周期、再读其余各项；
  判据只认这两个码，别的码（中断的、充不上的、空）仍然红。
- `green-delay-1/` 证明改后的判据在读到推进后的原因时判绿；`green-1/` 证明不注入时照常绿。
