## 生产切换（2026-10-14 晚）：三车上线、首趟开闸、站点超时、关闸

证据在控制端 `F:/8005-scratch/cutover-ev/production-20261014/`。

**agv03、agv01 的第 20～21 步**（用户逐步授权，agv01 每一步先问过）：06 号重装到 `d495746…`，IO 改指 `192.168.71.150:502`，车上暂存目录已删；逐仓核对 `agv03-run1`、`agv01-run1` 都是一次 8/8；FieldOps `verify` 都返回 OK、ready；19:59、20:10 分别起栈，握手 READY。三车 `status` 全部是 `ready=true`。

**IO 核对**（20:13）：`20-check-production-io.ps1` 三车都报 `IO_CHECK … OK`，记录在 `io-check.json`。看板积压里 `SLOT_CONFIGURATION_NOT_CONFIRMED` 为 0 条。

**agv03 充电**：19:55 之后，用户本人在 RIoT 上手工派 agv03 去 211 充电。v2 里没有出现人工充电保持、外来单或意图。读代码看，人工充电保持只有三种触发原因，这次都不满足。

**每区参数与首趟区表**（脚本都先经 C11 审过）：
- REQ-0198：两区都是 65000（version 1）。
- 20:21:45 导入小表，只有 N2-4、N4-1 两个区。20:21:47 受理 `Q26096572-7`（STAGING_TO_WIRE，2QHE71），20:21:49 受理 `Q26097573-2`（WIRE_TO_GATE，2QHE89）。
- 20:21:52 导入只有表头的空表，停止受理。累计受理 2 条。

**开闸**（20:23，用户授权，带 `-IoCheckRecord`）：两车出发，RIoT 单已确认。行驶中两车会话为 `RECOVERY_REQUIRED [DEPARTURE_UNSAFE]`，到站后恢复 READY，这是已知现象。开闸重启时出现一条 EF ConnectionError(20004)，另有 agv02 会话代数从 4 变到 5，那一轮让出了一次（control-server#334），都已自行恢复。

**两车都站点超时**：agv02 20:25 到 217，agv01 20:29 到 N4-1。现场没人、也没料，所以没人扫码。分别在 20:29:52、20:34:16 退回待派（release 1/2，日志 2310）。之后待派队列里这两条的原因码显示为 `VEHICLE_TASK_TYPE_NOT_ADMITTED`，没有重新派出，原因已报 C11 查。

**关闸**（20:38，用户决定）：闸门开着、夜里现场没人时，v2 会自己派车去充电或重派这两条需求，不符合「动车必须现场有人」。关闸后回读：闸门关、没有在途旅程、没有意图；两条受理需求仍是 Accepted。明早要重跑一次只读 IO 核对，再放开区表、开闸。

**用户对出发规则提的要求**已报 C11：全部装完立即出发；部分装好时屏幕上给出「现在出发」。现状是装完还要等 `StationDepartureWaitTimeout` 5 分钟的修正窗口，车载端也没有「现在出发」入口。

**已接受的风险**：Warning 2272 `STATION_CLEARANCE_ENTRY_NOT_OFFERED`。原因是生产定义没开 VehicleFaultRecovery，车在桩上充不进电时会停在 ORDER_HANG。今晚接受这个风险，生产稳定后再评估。
