# D 段：control-server#336 产品自己发出的急停——2026-10-11 凌晨 agv02

## 结论

**走通（在调度 10-10 定的范围内）。**车空载行驶途中，模拟器把 1 号仓锁反馈钉成 0，服务端约 1 秒内判出 `VEHICLE_DOOR_NOT_PROVEN_LOCKED`，先让 RIoT 暂停本单（`OrderHold`，RIoT 已确认），再因停稳证明不足（`STOP_PROOF_POSITION_UNKNOWN,STOP_PROOF_TOO_FEW_SAMPLES`）**只发一次** `triggerEmergency`，RIoT 进入 `CAN_RECOVER`，车停。锁反馈改回 AUTO 后，服务端**自己**发 `cancelEmergency`，RIoT 急停回 `OK`，车仍停着、单仍暂停。`vehicleFaultRecovery` 未开，故障事实留在库里。

收尾走了方案第 3 节的现场兜底：关闸被拒（`GATE_CLOSE_REFUSED_IN_FLIGHT`，有一个带故障的在途旅程）→ 用户授权停 V2 服务 → V2 停着时由 RIoT 人员结束本段 `W2G-` 单 → 00:48 RIoT 无涉及 agv02 的非终态单。用户另授权把 V2 启动类型改为 Manual，防止 factory01 重启时带着「闸门开」的配置自起。

## 身份

| 项 | 值 |
| --- | --- |
| 服务端 | v2 并行实例 fp/v2-impl@2fe490d7；`vehicleFaultRecovery.enabled=false` |
| 车载端 | agv02 ca89ef8f；IO＝模拟器 |
| 需求 | d0000566-0000-0000-0000-000000000001（WIRE_TO_GATE，N1-3，W2-1011-D-ESTOP） |
| RIoT 订单 | W2G-d0000566-0000-0000-0000-000000000001-PICKUP-1（order-2108959982044184576） |
| 授权 | `00-authorization.md`：开段「可以」；触发由用户在现场说「可以触发」；停 V2「停 V2 服务，RIoT 人员结束那张单」；改 Manual「单已结束，V2 改手动」 |

## 时间线（北京时间）

| 时刻 | 事件 |
| --- | --- |
| 00:32:30 | 段前实读：车在关卡 210，空载，电量 59%，急停 OK，会话 READY，闸门关 |
| 00:35:05 | 需求受理，等闸门 |
| 00:36:54 | 开闸；00:36:59 RIoT 开始执行，车离开 210 |
| 00:37:28 | 用户说「可以触发」后，模拟器 1 号仓 lock-feedback-override `FIXED_0`（`04`） |
| 00:37:29 | 2194 `LogDoorsNotProvenLocked` |
| 00:37:30 | 9200 故障等级；`OrderHold` 下发（00:37:32 RIoT 确认）；`triggerEmergency` POST 200（唯一一次）；9100 `Action=Triggered`；9101 `EMERGENCY_STOP_UNCONFIRMED`、9201 |
| 00:37:32 | 9100 `Action=LatchConfirmed`（确认闩锁，不是第二次触发） |
| 00:37:38 | RIoT `emergencyState=CAN_RECOVER`，`MT_PAUSED` |
| 00:38:02 | 模拟器锁反馈改回 `AUTO`（`05`） |
| 00:38:04 | 服务端 `cancelEmergency` POST 200；9101 `EMERGENCY_RELEASE_UNCONFIRMED`（等 RIoT 回读） |
| 00:38:08 | RIoT 急停 `OK`，车仍 `MT_PAUSED`，单仍在 |
| 00:41:xx | 关闸被拒 `GATE_CLOSE_REFUSED_IN_FLIGHT`（`08`） |
| 00:42:33 | 停 V2 服务（原 pid 16492 退出；MVP pid 15624 不变，`09`） |
| 00:43:48 | V2 启动类型改 Manual；RIoT 仍显示该单非终态（`10`） |
| 00:46:17 | 车被现场挪动（INNER_FORCE_IDLE），单仍非终态（`11`） |
| 00:48:07 | RIoT 无涉及 agv02／d0000566 的非终态单；车 (-47720,4343)，IDLE，急停 OK，电量 56%（`12`） |

## 判据（对照 L2 `in-transit-door-not-locked` 的形状）

| 判据 | 结果 | 依据 |
| --- | --- | --- |
| 行驶中门未证明锁闭被判出并交给故障模型 | 通过：2194 → 9200 | `07-estop-events.txt`、`06` 日志段 |
| 先暂停单 | 通过：`RiotOrderCommandAudit` `OrderHold` Confirmed，reason `VEHICLE_DOOR_NOT_PROVEN_LOCKED` | `06` |
| 不能证明停稳时触发急停，恰好一次 | 通过：`triggerEmergency` 一次；第二条 9100 为 `LatchConfirmed` | `07` |
| 停稳走产品判定（REQ-0247） | 本次以 `STOP_PROOF_*` 不足判「不能证明停稳」→ 急停闩锁 `CAN_RECOVER`；`VehicleFaultStates.StopProven=0` | `06`、`07` |
| 门锁恢复后自动解除急停 | 通过：`cancelEmergency` 00:38:04，RIoT 00:38:08 回 OK；单仍暂停、车未动 | `07`、监看 |
| 故障事实留存 | `VehicleFaultStates` `SuspectedBlocked`，generation 1，未清除（未开 vehicleFaultRecovery，按范围） | `06` |

## 留下的状态（下次启动 V2 之前必须处理）

- V2 服务 **Stopped、启动类型 Manual**；配置文件里 `RiotCreateDispatch.enabled` 仍是 **true**（关闸被拒，停服务前未能改）。
- 库里有一个未结束旅程 `journey:d0000566-…`（`AwaitingPickupArrival`，`VEHICLE_DOOR_NOT_PROVEN_LOCKED`）与故障行 `SuspectedBlocked`；其 RIoT 单已由现场在 V2 停止期间结束。V2 再启动时会把它看成「自己的单被结束」，自动重建会被故障挡住；按调度 10-10 定，含 cs#44 的下次部署换新库，这些随旧库留存。**不要不处理就直接启动 V2。**
