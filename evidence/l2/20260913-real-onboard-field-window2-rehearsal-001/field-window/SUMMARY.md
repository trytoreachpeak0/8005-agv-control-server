# 现场窗口二证据：完整闭环、取消订单、两趟之间自动充电、车静止时服务重启

**注意：本次按 `-SublotWaitMinutes 1` 判场景 T，且没有读到生产配置，只能算彩排，不能当现场窗口的证据。**





结论：**PASS**

对应地图票 [现场窗口二](https://github.com/trytoreachpeak0/8005-agv-program/issues/20)。

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T031515472Z` |
| 窗口 | `FW-FL2` |
| 现场 | L2 真装置彩排（本机） |
| 现场人员 | 无人到场，FieldOperator.psm1 run 20260913T031233678Z |
| agvId | `AGV-L2-001` |
| IO | `127.0.0.1:58412`（SIMULATOR） |
| 站点期限 | 1 分钟 |
| 旅程 | `c6328bb8-71cd-2f5c-a707-d2568d496cf7`（T/X/NE）、`2fb45d70-bb9e-3d51-bb03-3d3b749e9203`（充电之后） |
| 数据库 | `local-snapshot` |

## 判据

| 判据 | 说明 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- | --- |
| FL2-T-01 | 到站不录入 SUBLOT：需求被服务端终结为 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_STATION_TIMEOUT 永久抑制 | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 抑制键 = 需求键` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 键一致` |
| FL2-T-02 | 终结之前一次仓位操作都没有下发——没人扫码就没有装货 | PASS | `0` | `0` |
| FL2-T-03 | 站点期限是 1 分钟：车载端拿到的期限距驱动脚本看到服务端开始等 SUBLOT 不超过 1 分钟、且不短于它 60 秒以上 | PASS | `0–60 秒` | `59.5 秒` |
| FL2-T-04 | 到期才结算、没有无限拖着：结算被看到的时刻落在期限之后 0–180 秒 | PASS | `0–180 秒` | `2.2 秒` |
| FL2-T-05 | 车被释放：结算后旅程自己离开这一站，这趟旅程最终 Completed、派车租约已释放——没有停在 AwaitingSublot | PASS | `离站 / Completed / 租约已释放` | `1/AwaitingDepartureSafety / Completed / 已释放` |
| FL2-X-01 | 到站还没装货就取消：需求 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_OPERATOR 永久抑制（判的是服务端库，不是自动化面的返回） | PASS | `Cancelled / CANCELLED_BY_OPERATOR / 抑制键 = 需求键` | `Cancelled / CANCELLED_BY_OPERATOR` |
| FL2-X-02 | 取消发生在装货之前：这条需求没有任何仓位操作 | PASS | `0` | `0` |
| FL2-X-03 | 取消用的是车载端界面那一刻真的给出的按钮（availableRecoveryActions 含 LOAD_CANCELLATION），之后旅程自己离开这一站 | PASS | `LOAD_CANCELLATION 在按钮里 / 离站` | `LOAD_CANCELLATION / 2/AwaitingDepartureSafety` |
| FL2-NE-01 | 关门时货还在：车载端自己把这一仓重开了 2 轮（该仓 UNLOCKING 至少 3 次） | PASS | `>= 3` | `3` |
| FL2-NE-02 | 重开 2 轮之后卸货仍在进行（checkpoint ne-reopened）：操作 Prepared、旅程停在 AwaitingUnloadResult、需求没有被取消 | PASS | `Prepared / AwaitingUnloadResult / 需求未取消` | `Prepared / AwaitingUnloadResult / Accepted` |
| FL2-NE-03 | 唯一的出口是取空：卸货 Committed，这个 attempt 只有 COMPLETED 结果，需求 Succeeded | PASS | `Committed / 仅 COMPLETED / Succeeded` | `Committed / COMPLETED / Succeeded` |
| FL2-N-01 | 窗口里的每一趟旅程都走到 Completed，派车租约都已释放 | PASS | `全部 Completed / 已释放` | `c6328bb8=Completed/已释放; 2fb45d70=Completed/已释放` |
| FL2-N-02 | 完整闭环至少走通一次：一条需求录了 SUBLOT、装货提交、过出车前安全检查、卸货提交、需求 Succeeded（装货能提交即缺陷 20260829 现场未复现） | PASS | `至少一条需求全链路走通` | `38725cd9: SUBLOT✓ 装Committed 安检✓ 卸Committed; 3c592033: SUBLOT✓ 装Committed 安检✓ 卸Committed; 7f76f720: SUBLOT✓ 装Committed 安检✓ 卸Committed; e96ac77b: SUBLOT✓ 装Committed 安检✓ 卸Committed` |
| FL2-N-03 | 每一条 Succeeded 的需求都是全链路走通的——没有哪一条跳过了 SUBLOT、安全检查或卸货提交 | PASS | `4 条都走通` | `4 条走通` |
| FL2-CH-01 | 上一趟旅程结束之后才起充电行程，触发时电量低于当时生效的触发线 | PASS | `创建于 09/13/2026 11:14:23 之后 / 触发电量 < 30` | `2026-09-13 03:14:28.9180361+00:00 / 15` |
| FL2-CH-02 | 充电行程派了一条去充电桩 211 的 TO_CHARGER 单并已确认 | PASS | `TO_CHARGER / 211 / CONFIRMED` | `TO_CHARGER / 211 / CONFIRMED` |
| FL2-CH-03 | 车到桩并真的接上电：行程进入 Charging，驱动脚本全程没有看到 CHARGER_NOT_ENGAGED 或任何 CHARGER_* 阻塞 | PASS | `进入 Charging / 无 CHARGER_* 阻塞` | `AwaitingChargerArrival -> Charging -> Completed` |
| FL2-CH-04 | 充到恢复线释放：行程 Completed，释放电量 >= 80 | PASS | `Completed / >= 80` | `Completed / 80` |
| FL2-CH-05 | 释放之后接着受理下一单：下一趟旅程在充电行程结束之后创建，两趟之间只充了这一次电 | PASS | `有下一趟旅程 / 两趟之间 1 次充电` | `2fb45d70 创建于 2026-09-13 03:14:42.9748784+00:00 / 1 次` |
| FL2-R-00 | 至少观测到一次车静止时的服务重启（缺陷 20260908 的发现条件：真实服务重启加一台真车） | PASS | `>= 1` | `2` |
| FL2-R1-01 | 重启后新会话（generation > 重启前的 1）回到 Ready，checkpoint r1-ready 上的会话行同样是 Ready | PASS | `generation > 1 / Ready` | `1/Ready(READY) -> 2/Ready(READY) / checkpoint: 2/Ready` |
| FL2-R1-02 | 从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `0–60 秒` | `2.7 秒` |
| FL2-R1-03 | 重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `Completed` | `Completed` |
| FL2-R2-01 | 重启后新会话（generation > 重启前的 2）回到 Ready，checkpoint r2-ready 上的会话行同样是 Ready | PASS | `generation > 2 / Ready` | `2/Ready(READY) -> 3/Ready(READY) / checkpoint: 3/Ready` |
| FL2-R2-02 | 从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `0–60 秒` | `6.7 秒` |
| FL2-R2-03 | 重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `Completed` | `Completed` |
| FL2-W-01 | 现场记录由驱动脚本按实际动作写出，IO 是车上的 slots-simulator（无人到场） | PASS | `drivenBy 非空 / SIMULATOR` | `drivenBy=FieldOperator.psm1 run 20260913T031233678Z / SIMULATOR` |

## 实测事实

| 项 | 值 |
| --- | --- |
| `ioKind` | `SIMULATOR` |
| `minimumHoldMinutes` | `20` |
| `drivenBy` | `FieldOperator.psm1 run 20260913T031233678Z` |
| `sublotWaitMinutes` | `1` |
| `journeyIds` | `c6328bb8-71cd-2f5c-a707-d2568d496cf7, 2fb45d70-bb9e-3d51-bb03-3d3b749e9203` |
| `scenesOwed` | `T, X, NE, N, CH, R` |
| `T.demand` | `eb380b39-f6a1-40f8-bea9-6077bd05f4dc / L2-FW2-J1-N1-3-20260913T031233678Z / 停靠 1` |
| `X.demand` | `374b8f9b-12a4-46d9-80fb-9284333783e1 / L2-FW2-J1-N2-6-20260913T031233678Z / 停靠 2` |
| `X.faceAnswer` | `HTTP 200` |
| `NE.attempt` | `f9b0be6c-e70d-b052-80f9-00422c873733 / 仓 3 / UNLOCKING=3 WAITING_OPERATOR=3` |
| `CH.thresholds` | `出厂 30/80` |
| `CH.seen` | `AwaitingChargerArrival -> Charging -> Completed` |
| `R1.series` | `1/Ready(READY) -> 2/Ready(READY)` |
| `R2.series` | `2/Ready(READY) -> 3/Ready(READY)` |

## checkpoint 序列

场景 NE 的「重开几轮之后仍在进行」与场景 R 的「重启之后会话 Ready」都是会被后面的写入覆盖掉的时刻，
只能由当时抓下的 checkpoint 回答。

- `snapshots/01-00-ready/`
- `snapshots/02-t-settled/`
- `snapshots/03-x-settled/`
- `snapshots/04-ne-reopened/`
- `snapshots/05-r1-ready/`
- `snapshots/06-charge-dispatched/`
- `snapshots/07-charge-charging/`
- `snapshots/08-charge-released/`
- `snapshots/09-r2-ready/`
- `snapshots/10-finalize/`

## 目录内容

- `assertions.json` —— 机器可读的判据结论，含现场记录原文与实测事实
- `timeline.jsonl` —— 一行一次 checkpoint，只追加
- `logs/` —— 每次远端拷贝的输出
- `snapshots/<序号>-<label>/` —— 每个 checkpoint 当时的库行、两端安装清单、生产配置里的门与充电线、车载端日志

**整个 `controlserver.db` 不在这里**。它是生产库，完整副本落在 `C:\Users\szy\w2g-stage\field` 下的 `FW-FL2-<runId>`。

## 这份证据证明了什么，没证明什么

**证明了**：在 `AGV-L2-001`（L2 真装置彩排（本机））上、车走真实线路、IO 接 slots-simulator（`127.0.0.1:58412`）时，
一趟 WIRE_TO_GATE 旅程从受理到关卡收尾的软件闭环；决策 7 的站点期限在生产包里真的会终结一条没人扫码的需求并放车；扫码前取消；卸货没取空时没有取消分支；没有未完成旅程时车会自己去 211 充电、到恢复线后接单；车静止时服务重启后会话自己回到 Ready。

**没有证明**：

- **出厂触发线与恢复线（30% / 80%）这两个数本身。**充电那一幕按用户 2026-09-11 的决定临时抬线（见 出厂 30/80），证的是机制，不是出厂阈值；出厂值只由 L2 `auto-charge-endurance` 覆盖。
- **光幕极性、锁反馈时序与机械弹开。**IO 是模拟器，与现场窗口一（无人）同一个缺口，见地图 Out of scope。
- **另外两台车。**
