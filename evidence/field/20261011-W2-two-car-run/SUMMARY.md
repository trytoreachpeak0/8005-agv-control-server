# 两车段：③b 让站、④b 两车等待点独占——2026-10-11 晚 agv02＋agv03

服务端 `83a76689`（fp/v2-impl，含 cs#600 `4051f328` 与 cs#616），车载端 `270c20d0`，新库 newdb5，两车定义（车队表 agv02 RIoT 59、agv03 RIoT 60），每车独立的出发安全投影凭据。部署见 `../20261011-W2-prep6-two-car/`，方案见 `../20261011-W2-two-car-prep/PLAN.md`。仓位 IO 两车都接模拟器，条码自动录入，测试等待 1:30／2:00。授权见 `00-authorization.md`（用户原话「确认 agv01 不碰我们的路段，有人盯着，可以开闸」）。

## 结论

**③b 让站走通；④b 两车等待点独占走通（单向证据，见下）；cs#600 在真车上验证。**全程没有 00002，没有人工处置。

| 判据 | 结果 | 出处 |
| --- | --- | --- |
| cs#600：两车各读到自己的投影，都能就绪 | 部署探针 `vehicle-safety 200`，vehicleKey 各自对应；19:11:04 agv02、19:11:32 agv03 `READY`；日志无 401／403（下午 agv03 在两车实例里永远不就绪） | `../20261011-W2-prep6-two-car/09-*`、`22-readiness-two-cars.txt` |
| 开闸时两车各占一个等待点 | 19:14:11 2225：agv02 占 216、agv03 占 214 | `03-after-gate-open.txt` |
| 甲（N1-3，1 花篮）给一台车，装完进入持货等单 | 19:14:35 受理，进 agv03 的旅程；19:19:02 条码自动录入 `SUBMITTED_ONCE`；19:19:14 `CARGO_HOLDING_WAIT`（期限 19:21:14） | `04`、`08`、`09` |
| 乙（同站，32 箱＝4 花篮）只能给另一台车 | 19:19:22 注入；19:19:23.1636885 由 **agv02** 受理（`AcceptedDemands.AcceptedAt`），目标仓 `[1,2,3,4]` | `10`、`14` |
| **让站**：持单车结束等单，触发记另一台车，时刻不早于受理、早于持货到期 | agv03 `LoadingPhaseState=CLOSED`、`LoadingClosedReason=WAITING_STATION_YIELD`、`YieldTriggeredByVehicleKey`＝agv02、`YieldTriggeredAt=19:19:23.1636885`（与乙受理同一次提交），比持货期限早约 1 分 51 秒 | `11-watch-yield.txt` |
| 两条都卸货 | agv03 19:24:01 在 210 卸 1 号仓；agv02 在 N1-3 一次一扇装 1～4 号仓、到 210 卸完；甲、乙 `Status=UNLOADED` | `13`、`12`、`18`、`17` |
| 固定站 210 独占 | 19:23:44 2212 `FIXED_TASK_STATION_HELD_ON_APPROACH`：agv02 前往 210 时，210 由 agv03 持有 | `20-log-idle-returns-and-points.txt` |
| **④b**：两车不占同一个等待点 | 19:24:08 agv03 空闲返回预留 216（代价 6170 mm）→ 19:24:50 到 216；19:30:54 agv02 预留 **215**（代价 7380 mm，216 此时被 agv03 占用）→ 19:31:36 到 215；独占表最终 216＝agv03、215＝agv02 | `17`、`20` |
| 收尾 | 19:32:10 关闸 PASS，MVP 不变 | `19-gate-close.txt` |

## 说明

- **④b 是单向证据**：证明了「被占的点不再分给另一台车、两车始终各占一点」。没有证明「216 对 agv02 本来更近」——日志只给了 agv02 到 215 的代价，没给它到 216 的代价。两车也不是同一轮争抢（agv03 先回、agv02 六分多钟后回）。
- 开闸前 agv01 在 25 号图站 41 报错（`sysState=ERROR`、电量 8%），挂着一张去 25 号图 212→211 的充电单；没有碰它，用户确认它不碰本段路线（`01-riot-before-gate.txt`）。
- 部署时 V2 第一次带运行时启动失败，9401 `WAITING_POINTS_FEWER_THAN_VEHICLES`：新库还没导入等待点。停着服务先导入等待点再启动解决（`../20261011-W2-prep6-two-car/12`、`13`）。
- 行驶途中会话 `RECOVERY_REQUIRED`（`DEPARTURE_UNSAFE`），到站恢复，已知现象。
