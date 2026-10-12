# cs#566 续场三：投运阈值低电进桩 + 充电中途断电（G4），2026-10-12 上午

V2 并行实例：服务端 `83a76689`，车载端 `270c20d0`，库 newdb5，两车定义。只动 agv03。每次起栈、开闸、拉闸、合闸都由用户在会话里单独授权，现场有人。方案见 `PLAN.md`，调度 C10 核准。

## 结论

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 1. 投运阈值低电进桩（v1：30/80，211） | **通过**。09:54:37 agv03 电量 25% < 30，记 2240；09:55:56 建单（39 字符）；09:58:02 到桩并开始充电。第二次是 D 段：10:14 跌到 29%，10:16:55 到桩，10:46:43 充到 80（2260），10:46:57 离桩并释放 211（2248，`CHARGING_DEPARTED`），回 215。两个周期都是 `ChargingPolicyVersion=1` | `00`、`20`～`25`、`38`～`43` |
| 2. 充电中途断电（G4，cs#407） | **通过，走的是中断那一路**。10:02 现场拉闸。RIoT 实测：10:02:11 仍是 `CHARGING`，10:02:18 起变为 `batteryState=NO_CHARGE`、`sysState=IDLE`，电量停在 31%，车留在 211。10:03:16 记 2276 `INTERRUPTION_CONFIRMED`：从第一次读到非充电起约 62 秒，从拉闸起约 74 秒。在同一次保存里写入桩暂停、车辆资格暂停、清桩记录，周期转为 `CLEARING`，没有新单，车不动 | `26`～`28` |
| 清桩 | **通过（Host 入口）**。用户在 RIoT 把车挪到 215 后，10:10:05 调 `POST /api/charging/v1/station-clearances` → 200 `CONFIRMED`，`stationReleased=true`；周期结束原因 `CHARGING_UNABLE_TO_CHARGE_CLEARED`。没有用车载屏入口，原因见 `31a` | `29`～`32` |
| 桩恢复、车辆资格恢复 | **通过**。10:12 合闸。10:12:07 `station-recoveries` → 200 `RECOVERED`；10:12:09 `vehicle-eligibility-recoveries` → 200 `RECOVERED`。回读：未恢复的暂停 0 行 | `33`～`36` |
| 3. 自动开往等待点清桩（REQ-0178，cs#409） | **未做**。前提未满足：白名单 1.2 措辞，以及车载端途中清桩入口未核实 | `PLAN.md` |

## 准备段与现场发现

- 改动前的值都记了（`02`、`03`）：测试等待 5:00/5:00 改为 1:30/2:00；`VehicleFaultRecovery` false 改为 true，并把凭据写入 V2 服务环境（只记键名）；`onboardClearanceEntryDeclared` false 改为 true；agv03 `unableToChargeEntryEnabled` false 改为 true。
- agv03 打开 `recoveryResumeEnabled` 后车载端起不来，报「Production环境未提供恢复管理员凭据环境变量」：agv03 机器上没有 `CONTROL_SERVER_RECOVERY_PROOF`，agv02 上有（`10`）。改回 false 后正常起来（`12`）。
- **更正**：上午说「清桩入口不依赖两个开关」。实际是它不依赖开关，但依赖同一个恢复凭据，所以 agv03 屏上不出现「确认清桩」按钮（`30`、`31a`）。agv03 缺凭据属于部署缺口，调度另记。
- 车刚开机时，RIoT 报 `movementState=MT_NA`，V2 不认停稳，会话一直停在 `DEPARTURE_UNSAFE`。用户在 RIoT 上手动把 agv03 从 214 挪到 215 后，才 READY（`15`～`19`）。调度已开 cs#639。
- 10:12:55 D 段授权前先关了闸：agv03 电量已降到 30，再低一点就会被自动派单（`37`）。
- MVP pid 的变化（09:40 为 17140，11:53 为 10912）来自 agv01 运维会话的重启，与本测试无关，调度已核。

## 收工

见 `../20261012-cs566c-closeout/SUMMARY.md`。
