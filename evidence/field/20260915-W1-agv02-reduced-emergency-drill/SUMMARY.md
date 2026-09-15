# W1 空载急停演练（缩减版）证据摘要 `20260915T063522Z-0760`

| 项 | 值 |
| --- | --- |
| 票据 | 批次 2 票 19（W1 空载急停演练），缩减版 |
| runId | `20260915T063522Z-0760` |
| 车辆 | agv02 `BROKERX-f38975561adf46ccb1d2f23833c7d0e4` |
| 地图 | map 25（`老厂前线new`） |
| RIoT 地址 | `http://172.19.206.222:8888` |
| 初始化时刻／主机 | 2026-09-15 14:35:22.849 +08:00／`LAB-WIN-01` |
| 演练单 | `W1-DRILL-20260915T063522Z-0760` → orderId `order-2099750634810114048`，站 210 → 212 |
| 收尾顺序 | 发令 → 取消演练单 → 解除急停（解除前演练单必须已是终态） |
| 摘要生成时刻 | 2026-09-15 15:00:43.921 +08:00 |

## 一、判定

| 判据 | 结论 | 依据 |
| --- | --- | --- |
| RIoT 侧确实停车 | **未证实** | 发令前 `speed=0`、`movementState=MT_RUNNING`、`currentStationId=0`；调用结果 `Accepted`；发令后 2157 ms 读到 `emergencyState=CAN_RECOVER`；观察窗内没有停稳证据；共 59 个采样。 |
| 8005 只发一次 | **PASS** | 本 run 在发令前写下的 `triggerEmergency` 记录（`commands.jsonl` 中 `phase=SENDING`）1 条；`wire.jsonl` 中实际发出的 `POST …/triggerEmergency` 1 次。产品路径（`EmergencyStopSupervisor`）的「只发一次」不由本演练证明，由 L2 场景 `emergency-stop-single-trigger` 在服务端 CI 三连跑证明：`evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-01`、`evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-02`、`evidence/l2/20260914-ci-34815736635-emergency-stop-single-trigger-03` |
| 演练单已清理（先于解除） | 未做 | 本 run 没有取消演练单。 |
| 急停解除生效 | 未做 | 本 run 没有调用 `cancelEmergency`。 |

票 19 要求停车事实由**现场观察与 RIoT 侧证据双向确认**：上表只是 RIoT 侧证据，现场目视结论填在第三节。
停稳判据：连续至少 3 个采样、跨度至少 1 秒，每个采样 `speed=0`、`movementState` 已上报且不是 `MT_RUNNING`、`currentMap` 与 `currentStationId` 不变；读数失败会打断连续。车辆卡片不带坐标，「位置不变」只能按站点号判定。同时记下产品 `ReadMotion` 是否也会把这段读成 `NotMoving`（它只认 `MT_FINISHED`／`MT_PAUSED`）。

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
| 2026-09-15T14:35:22.9261429+08:00 | `init` | OK |  |
| 2026-09-15T14:35:23.8797032+08:00 | `preflight` | OK | preflight passed on LAB-WIN-01 |
| 2026-09-15T14:35:24.2975101+08:00 | `status` | OK |  |
| 2026-09-15T14:36:24.4466614+08:00 | `status` | OK |  |
| 2026-09-15T14:42:14.9881076+08:00 | `preflight` | OK | preflight passed on LAB-WIN-01 |
| 2026-09-15T14:42:15.4236481+08:00 | `status` | OK |  |
| 2026-09-15T14:42:15.859521+08:00 | `create-move` | **发出** | 即将发出 `byDefaultMissions` → `W1-DRILL-20260915T063522Z-0760` |
| 2026-09-15T14:42:16.1229126+08:00 | `create-move` | OK | order order-2099750634810114048 for agv02: station 210 -> 212; next: watch-moving |
| 2026-09-15T14:44:16.7736857+08:00 | `watch-moving` | WINDOW_MISSED | no moving-between-stations window within 120 s (226 samples) |
| 2026-09-15T14:44:17.3527705+08:00 | `status` | OK |  |
| 2026-09-15T14:44:40.967342+08:00 | `status` | OK |  |
| 2026-09-15T14:48:57.7624822+08:00 | `watch-moving` | READY | the vehicle is moving between stations |
| 2026-09-15T14:48:58.1400291+08:00 | `trigger` | **发出** | 即将发出 `triggerEmergency` → `BROKERX-f38975561adf46ccb1d2f23833c7d0e4` |
| 2026-09-15T14:49:18.3894948+08:00 | `trigger` | NOT_CONFIRMED | triggerEmergency was sent once and this run will NOT send it again. The latch and/or the stop were not both observed in the window -- watch the vehicle, run status, and hand over to RIoT staff if it does not stop. |
| 2026-09-15T14:49:18.9740452+08:00 | `status` | OK |  |
| 2026-09-15T14:49:51.0751496+08:00 | `status` | OK |  |
| 2026-09-15T14:53:09.7319101+08:00 | `status` | OK |  |
| 2026-09-15T14:53:20.2933961+08:00 | `status` | OK |  |
| 2026-09-15T14:53:30.8321076+08:00 | `status` | OK |  |
| 2026-09-15T14:53:41.3603778+08:00 | `status` | OK |  |
| 2026-09-15T14:53:51.9178683+08:00 | `status` | OK |  |
| 2026-09-15T14:54:02.4609185+08:00 | `status` | OK |  |
| 2026-09-15T14:54:13.0125618+08:00 | `status` | OK |  |
| 2026-09-15T14:54:23.5502188+08:00 | `status` | OK |  |
| 2026-09-15T14:54:34.0959954+08:00 | `status` | OK |  |
| 2026-09-15T14:54:44.6182901+08:00 | `status` | OK |  |
| 2026-09-15T14:54:55.1410868+08:00 | `status` | OK |  |
| 2026-09-15T14:55:05.6827638+08:00 | `status` | OK |  |
| 2026-09-15T14:55:16.2215393+08:00 | `status` | OK |  |
| 2026-09-15T14:55:26.7392366+08:00 | `status` | OK |  |
| 2026-09-15T14:55:37.2634851+08:00 | `status` | OK |  |
| 2026-09-15T14:55:47.772892+08:00 | `status` | OK |  |
| 2026-09-15T14:55:58.3037784+08:00 | `status` | OK |  |
| 2026-09-15T14:56:08.8371979+08:00 | `status` | OK |  |
| 2026-09-15T14:56:19.3763607+08:00 | `status` | OK |  |
| 2026-09-15T14:56:29.9330489+08:00 | `status` | OK |  |
| 2026-09-15T14:56:40.4719768+08:00 | `status` | OK |  |
| 2026-09-15T14:56:50.9873159+08:00 | `status` | OK |  |
| 2026-09-15T14:57:01.5037214+08:00 | `status` | OK |  |
| 2026-09-15T14:57:12.0351646+08:00 | `status` | OK |  |
| 2026-09-15T14:57:22.5428234+08:00 | `status` | OK |  |
| 2026-09-15T14:57:33.1107537+08:00 | `status` | OK |  |
| 2026-09-15T14:57:43.6161292+08:00 | `status` | OK |  |
| 2026-09-15T14:57:54.1462052+08:00 | `status` | OK |  |
| 2026-09-15T14:58:04.6683183+08:00 | `status` | OK |  |
| 2026-09-15T14:58:15.2219936+08:00 | `status` | OK |  |
| 2026-09-15T14:58:25.7735588+08:00 | `status` | OK |  |
| 2026-09-15T14:58:36.300913+08:00 | `status` | OK |  |
| 2026-09-15T14:58:46.84593+08:00 | `status` | OK |  |
| 2026-09-15T14:58:57.3812101+08:00 | `status` | OK |  |
| 2026-09-15T14:59:07.9032227+08:00 | `status` | OK |  |
| 2026-09-15T14:59:18.425398+08:00 | `status` | OK |  |
| 2026-09-15T14:59:28.9840245+08:00 | `status` | OK |  |
| 2026-09-15T14:59:39.5691537+08:00 | `status` | OK |  |
| 2026-09-15T14:59:50.1288572+08:00 | `status` | OK |  |
| 2026-09-15T15:00:00.677295+08:00 | `status` | OK |  |
| 2026-09-15T15:00:11.1990371+08:00 | `status` | OK |  |
| 2026-09-15T15:00:35.4386935+08:00 | `status` | OK |  |
| 2026-09-15T15:00:39.0229694+08:00 | `status` | OK |  |
| 2026-09-15T15:00:42.5647546+08:00 | `status` | OK |  |

## 五、RIoT 写调用计数（本 run）

| 调用 | 发令前记录（SENDING） | 线上 POST（wire.jsonl） |
| --- | --- | --- |
| `byDefaultMissions`（建单） | 1 | 1 |
| `triggerEmergency` | 1 | 1 |
| `CMD_ORDER_CANCEL`（订单命令端点） | 0 | 0 |
| `cancelEmergency` | 0 | 0 |

## 六、网络预检（最近一次）

| 项 | 值 |
| --- | --- |
| 时刻／主机 | 2026-09-15 14:42:14.976 +08:00／`LAB-WIN-01` |
| 结论 | 通过 |
| 路由 | `DIRECT` |
| 车辆卡片读 5 次 | 中位 17.1 ms，最大 87.5 ms |
| 未通过项 | 无 |

路由细节（下一跳、出口网卡、TUN 标记、系统代理）见 `timeline.jsonl` 的 `preflightRoute` 行。本工具的 HTTP 传输 `UseProxy=false`。

## 七、异常与偏离

- 发令后观察窗内没有得到停稳证据（连续 3 个静止采样、跨度 1 秒）。
- 发令后本 run 没有取消演练单。
- 发令后本 run 没有解除急停。
- 2026-09-15T14:44:16.7736857+08:00 `watch-moving` → WINDOW_MISSED：no moving-between-stations window within 120 s (226 samples)
- 2026-09-15T14:49:18.3894948+08:00 `trigger` → NOT_CONFIRMED：triggerEmergency was sent once and this run will NOT send it again. The latch and/or the stop were not both observed in the window -- watch the vehicle, run status, and hand over to RIoT staff if it does not stop.

## 八、本目录文件

- `drill-state.json`：本 run 做过什么；每次改写前的版本都追加在 `state-history.jsonl`。建单、发令、取消单、解除之前先写这里再调用。
- `commands.jsonl`：每条命令一行（参数、守卫逐项结果、结论），每次 RIoT 写调用发出前另有一行 `phase=SENDING`。
- `timeline.jsonl`：全部观测（车辆卡片、运动采样、闩锁、订单、站点、预检）。
- `wire.jsonl`：每个 HTTP 请求的方法、路径、状态码与耗时；不记请求头，API key 不进证据。
- `SUMMARY.md`：本文件；第三节由现场人员补填。
