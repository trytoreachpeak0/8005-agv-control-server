# W1 空载急停演练（缩减版）证据摘要 `20260915T070351Z-333b`

| 项 | 值 |
| --- | --- |
| 票据 | 批次 2 票 19（W1 空载急停演练），缩减版 |
| runId | `20260915T070351Z-333b` |
| 车辆 | agv02 `BROKERX-f38975561adf46ccb1d2f23833c7d0e4` |
| 地图 | map 25（`老厂前线new`） |
| RIoT 地址 | `http://172.19.206.222:8888` |
| 初始化时刻／主机 | 2026-09-15 15:03:51.722 +08:00／`LAB-WIN-01` |
| 演练单 | `W1-DRILL-20260915T070351Z-333b` → orderId `order-2099757039231303680`，站 210 → 212 |
| 收尾顺序 | 发令 → 取消演练单 → 解除急停（解除前演练单必须已是终态） |
| 摘要生成时刻 | 2026-09-15 15:12:27.182 +08:00 |

## 一、判定

| 判据 | 结论 | 依据 |
| --- | --- | --- |
| RIoT 侧确实停车 | **PASS**（RIoT 侧证据；现场目视结论见第三节） | 发令前 `speed=0.349`、`movementState=MT_RUNNING`、`currentStationId=0`；调用结果 `Accepted`；发令后 1117 ms 读到 `emergencyState=CAN_RECOVER`；发令后 1144 ms 起连续静止采样（结束时连续 4 个；产品读数 NotMoving：否；`stillWhileLatchedRunning=true`，即停稳用到了闩锁锁着时 `speed=0` + `MT_RUNNING` 计为静止）；共 7 个采样。 |
| 8005 只发一次 | **PASS** | 本 run 在发令前写下的 `triggerEmergency` 记录（`commands.jsonl` 中 `phase=SENDING`）1 条；`wire.jsonl` 中实际发出的 `POST …/triggerEmergency` 1 次。产品路径（`EmergencyStopSupervisor`）的「只发一次」不由本演练证明，由 L2 场景 `emergency-stop-single-trigger` 在服务端 CI 三连跑证明：`evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-01`、`evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-02`、`evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-03` |
| 演练单已清理（先于解除） | **PASS** | 取消时闩锁 `CAN_RECOVER`、车辆停稳（`stillWhileLatchedRunning=true`）；`CMD_ORDER_CANCEL` `order-2099757039231303680` 调用结果 `Accepted`；回读 `orderState=2`，终态：是。 |
| 急停解除生效 | **PASS** | 解除前演练单已是终态（`orderState=2`）；现场确认人 trytoreachpeak0（`trytoreachpeak0 stopped,empty,doors-closed`）；解除前闩锁 `CAN_RECOVER`、车辆停稳（`stillWhileLatchedRunning=false`）；调用结果 `Accepted`；发出后 3584 ms 读到 `emergencyState=OK`。 |

票 19 要求停车事实由**现场观察与 RIoT 侧证据双向确认**：上表只是 RIoT 侧证据，现场目视结论填在第三节。
停稳判据：连续至少 3 个采样、跨度至少 1 秒，每个采样 `speed=0`、`movementState` 已上报，且要么不是 `MT_RUNNING`，要么是 `MT_RUNNING` 但当时急停闩锁已知锁着（取该采样之前最近一次闩锁读数为 `CAN_RECOVER`／`CAN_NOT_RECOVER`；闩锁 `OK` 或未读到时 `MT_RUNNING` 不算静止）；`currentMap` 与 `currentStationId` 不变；读数失败会打断连续。车辆卡片不带坐标，「位置不变」只能按站点号判定。同时记下产品 `ReadMotion` 是否也会把这段读成 `NotMoving`（它只认 `MT_FINISHED`／`MT_PAUSED`），以及停稳是否用到了「闩锁锁着 + `MT_RUNNING` + `speed=0`」这一条（`stillWhileLatchedRunning`）。后一条来自 2026-09-15 现场：闩锁锁着、演练单未取消时 RIoT 持续报 `MT_RUNNING`、`speed=0`。

## 二、白名单破例（2026-09-14 用户批准）

2026-09-14，产品负责人（用户）在对话中批准：批次 2 票 19 不走 FAILED 产品路径，改做缩减版演练，并对 `vendor/8005-agv-program/docs/riot-call-allowlist.md` 第 1.5 节做**一次性破例，仅限本次演练、仅限 agv02**（`BROKERX-f38975561adf46ccb1d2f23833c7d0e4`）：

1. 8005 工具经白名单内的 `POST /api/order/v1/add/byDefaultMissions` 为 agv02 建一张移动单，让空载车在 map 25 的两个站点之间行驶；
2. 车在两站之间行驶时，工具向 agv02 发送**且只发送一次** `triggerEmergency`；
3. 工具观察 RIoT 确实让车停下，并读取急停闩锁 `emergencyState`（`CAN_RECOVER`／`CAN_NOT_RECOVER`）；
4. 车在急停锁着、停稳的状态下，以白名单内的订单命令 `CMD_ORDER_CANCEL` 取消演练单——只作清理，不代替停车；`CAN_NOT_RECOVER` 时同样取消；
5. 演练单进入终态，且现场人员确认车辆已停稳、车上无货、仓门已关（代替白名单要求的车载端确认）后，**仅当**闩锁为 `CAN_RECOVER` 时调用一次 `cancelEmergency`，并回查 `emergencyState=OK`；`CAN_NOT_RECOVER` 时禁止调用解除，转 RIoT 人工处理。

**收尾顺序是先取消演练单、再解除急停。**最初批准的写法是先解除、后取消单；同日用户裁定改为现在的顺序，理由：解除后 RIoT 可能让车接着开往终点，而现场人员此时正在车旁。工具以守卫强制这个顺序。

会让车移动、发急停、取消订单或解除急停的每一条命令，运行前都在对话中逐次单独授权。白名单文档本身不改。

## 三、现场记录（现场填写）

| 项 | 填写 |
| --- | --- |
| 日期 |  |
| 现场安全员（姓名） |  |
| 工具操作员（姓名） |  |
| RIoT 配合人（姓名） |  |
| 其他参与人 |  |
| 运行主机 | `LAB-WIN-01` |

| 时刻 | 事件 | 记录人 |
| --- | --- | --- |
|  | 空载确认：车上无货、仓内无产品 |  |
|  | 发令前目视：车在两站之间行驶 |  |
|  | 目视确认停车（停车位置） |  |
|  | 取消演练单后车辆状态（应仍停着） |  |
|  | 确认车辆停稳、无货、仓门全部关闭（`release --field-confirmed` 的依据） |  |
|  | 解除后车辆状态（应不再移动：演练单已是终态） |  |

现场目视结论（发令后车是否停下、停在哪里、与 RIoT 侧证据是否一致；取消单与解除后车是否保持不动）：

照片指针（照片本身不进 git，这里只留指针）：

- 空载车厢／仓位：
- 发令前车辆位置：
- 停车后车辆位置：
- RIoT 界面急停状态截图：
- 取消演练单后 RIoT 订单状态截图：
- 解除后 RIoT 界面截图：

## 四、命令记录

| 时刻 | 命令 | 结果 | 说明 |
| --- | --- | --- | --- |
| 2026-09-15T15:03:51.7801054+08:00 | `init` | OK |  |
| 2026-09-15T15:03:52.7875558+08:00 | `preflight` | OK | preflight passed on LAB-WIN-01 |
| 2026-09-15T15:03:53.1947411+08:00 | `status` | OK |  |
| 2026-09-15T15:07:42.6943807+08:00 | `create-move` | **发出** | 即将发出 `byDefaultMissions` → `W1-DRILL-20260915T070351Z-333b` |
| 2026-09-15T15:07:42.9845478+08:00 | `create-move` | OK | order order-2099757039231303680 for agv02: station 210 -> 212; next: watch-moving |
| 2026-09-15T15:07:51.8886801+08:00 | `watch-moving` | READY | the vehicle is moving between stations (2 consecutive samples with speed > 0.05 and no station) |
| 2026-09-15T15:07:52.2545051+08:00 | `trigger` | **发出** | 即将发出 `triggerEmergency` → `BROKERX-f38975561adf46ccb1d2f23833c7d0e4` |
| 2026-09-15T15:07:54.440958+08:00 | `trigger` | OK | RIoT latched and the vehicle stopped; next: cancel-order (the order is cancelled before the latch is released) |
| 2026-09-15T15:07:55.0141123+08:00 | `status` | OK |  |
| 2026-09-15T15:10:32.2647581+08:00 | `cancel-order` | **发出** | 即将发出 `CMD_ORDER_CANCEL` → `order-2099757039231303680` |
| 2026-09-15T15:10:32.4286856+08:00 | `cancel-order` | OK | drill order is terminal. Next: field staff confirm stopped / empty / doors closed, then release |
| 2026-09-15T15:10:33.0343513+08:00 | `status` | OK |  |
| 2026-09-15T15:12:22.8220066+08:00 | `release` | **发出** | 即将发出 `cancelEmergency` → `BROKERX-f38975561adf46ccb1d2f23833c7d0e4` |
| 2026-09-15T15:12:26.414599+08:00 | `release` | OK | latch released (emergencyState=OK); next: summarize |
| 2026-09-15T15:12:27.0188667+08:00 | `status` | OK |  |

## 五、RIoT 写调用计数（本 run）

| 调用 | 发令前记录（SENDING） | 线上 POST（wire.jsonl） |
| --- | --- | --- |
| `byDefaultMissions`（建单） | 1 | 1 |
| `triggerEmergency` | 1 | 1 |
| `CMD_ORDER_CANCEL`（订单命令端点） | 1 | 1 |
| `cancelEmergency` | 1 | 1 |

## 六、网络预检（最近一次）

| 项 | 值 |
| --- | --- |
| 时刻／主机 | 2026-09-15 15:03:52.774 +08:00／`LAB-WIN-01` |
| 结论 | 通过 |
| 路由 | `DIRECT` |
| 车辆卡片读 5 次 | 中位 19.6 ms，最大 224.3 ms |
| 未通过项 | 无 |

路由细节（下一跳、出口网卡、TUN 标记、系统代理）见 `timeline.jsonl` 的 `preflightRoute` 行。本工具的 HTTP 传输 `UseProxy=false`。

## 七、异常与偏离

- 停稳那段采样的 `movementState` 不在产品 `ReadMotion` 认可的「未移动」集合（`MT_FINISHED`／`MT_PAUSED`）里：产品路径不会把它读成已停住。
- `trigger`、`cancel-order` 的停稳证据用到了 2026-09-15 现场补充的规则（`stillWhileLatchedRunning=true`）：闩锁锁着时 `speed=0` + `movementState=MT_RUNNING` 计为静止。

## 八、本目录文件

- `drill-state.json`：本 run 做过什么；每次改写前的版本都追加在 `state-history.jsonl`。建单、发令、取消单、解除之前先写这里再调用。
- `commands.jsonl`：每条命令一行（参数、守卫逐项结果、结论），每次 RIoT 写调用发出前另有一行 `phase=SENDING`。
- `timeline.jsonl`：全部观测（车辆卡片、运动采样、闩锁、订单、站点、预检）。
- `wire.jsonl`：每个 HTTP 请求的方法、路径、状态码与耗时；不记请求头，API key 不进证据。
- `SUMMARY.md`：本文件；第三节由现场人员补填。
