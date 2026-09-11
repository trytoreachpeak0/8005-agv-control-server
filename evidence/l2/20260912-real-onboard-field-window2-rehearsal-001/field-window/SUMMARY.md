# 现场窗口一证据：三个操作员不作为场景在车上的 slots-simulator 上无人驱动验收



结论：**FAIL**

对应 [ADR-cross-0058](../../../8005-agv-program/docs/adr/cross/0058-slot-convergence.md) 的决策 1、2、4、5，
以及地图票 [现场窗口一（无人）](https://github.com/trytoreachpeak0/8005-agv-program/issues/45)。

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T160931987Z` |
| 窗口 | `FW-FL2` 装卸站收敛语义 |
| 现场 | L2 真装置彩排（本机） |
| 现场人员 | 无人到场，FieldOperator.psm1 run 20260911T160655450Z |
| agvId | `AGV-L2-001` |
| IO | `127.0.0.1:58412`（SIMULATOR） |
| 场景 C 等待下限 | 20 分钟 |
| 恢复窗口 | **未开启** |
| 数据库 | `local-snapshot` |

**这个窗口号与 `evidence/field/README.md` 里的 `W1`/`W2`/`W3` 不是一回事**：那三个是仓位配置就绪
那条线的窗口（车辆资格 / 多车与等待点 / 自动充电）。这里的 `FW-FL2` 是装卸站收敛语义的第一次现场窗口。

## 判据

| 判据 | 说明 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- | --- |
| FL2-T-01 | 到站不录入 SUBLOT：需求被服务端终结为 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_STATION_TIMEOUT 永久抑制 | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 抑制键 = 需求键` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 键一致` |
| FL2-T-02 | 终结之前一次仓位操作都没有下发——没人扫码就没有装货 | PASS | `0` | `0` |
| FL2-T-03 | 站点期限是 1 分钟：车载端拿到的期限距驱动脚本看到服务端开始等 SUBLOT 不超过 1 分钟、且不短于它 60 秒以上 | PASS | `0–60 秒` | `59.4 秒` |
| FL2-T-04 | 到期才结算、没有无限拖着：结算被看到的时刻落在期限之后 0–180 秒 | PASS | `0–180 秒` | `1.2 秒` |
| FL2-T-05 | 车被释放：结算后旅程自己离开这一站，这趟旅程最终 Completed、派车租约已释放——没有停在 AwaitingSublot | PASS | `离站 / Completed / 租约已释放` | `1/AwaitingDepartureSafety / Completed / 已释放` |
| FL2-X-01 | 到站还没装货就取消：需求 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_OPERATOR 永久抑制（判的是服务端库，不是自动化面的返回） | PASS | `Cancelled / CANCELLED_BY_OPERATOR / 抑制键 = 需求键` | `Cancelled / CANCELLED_BY_OPERATOR` |
| FL2-X-02 | 取消发生在装货之前：这条需求没有任何仓位操作 | PASS | `0` | `0` |
| FL2-X-03 | 取消用的是车载端界面那一刻真的给出的按钮（availableRecoveryActions 含 LOAD_CANCELLATION），之后旅程自己离开这一站 | PASS | `LOAD_CANCELLATION 在按钮里 / 离站` | `LOAD_CANCELLATION / 2/AwaitingDepartureSafety` |
| FL2-NE-01 | 关门时货还在：车载端自己把这一仓重开了 2 轮（该仓 UNLOCKING 至少 3 次） | PASS | `>= 3` | `3` |
| FL2-NE-02 | 重开 2 轮之后卸货仍在进行（checkpoint ne-reopened）：操作 Prepared、旅程停在 AwaitingUnloadResult、需求没有被取消 | PASS | `Prepared / AwaitingUnloadResult / 需求未取消` | `Prepared / AwaitingUnloadResult / Accepted` |
| FL2-NE-03 | 唯一的出口是取空：卸货 Committed，这个 attempt 只有 COMPLETED 结果，需求 Succeeded | PASS | `Committed / 仅 COMPLETED / Succeeded` | `Committed / COMPLETED / Succeeded` |
| FL2-N-01 | 窗口里的每一趟旅程都走到 Completed，派车租约都已释放 | PASS | `全部 Completed / 已释放` | `bd52ba9b=Completed/已释放; a1e6f039=Completed/已释放` |
| FL2-N-02 | 完整闭环至少走通一次：一条需求录了 SUBLOT、装货提交、过出车前安全检查、卸货提交、需求 Succeeded（装货能提交即缺陷 20260829 现场未复现） | PASS | `至少一条需求全链路走通` | `0848a67f: SUBLOT✓ 装Committed 安检✓ 卸Committed; 1834a85c: SUBLOT✓ 装Committed 安检✓ 卸Committed; 61b1b152: SUBLOT✓ 装Committed 安检✓ 卸Committed; f6e1dd1d: SUBLOT✓ 装Committed 安检✓ 卸Committed` |
| FL2-N-03 | 每一条 Succeeded 的需求都是全链路走通的——没有哪一条跳过了 SUBLOT、安全检查或卸货提交 | PASS | `4 条都走通` | `4 条走通` |
| FL2-CH-01 | 上一趟旅程结束之后才起充电行程，触发时电量低于当时生效的触发线 | PASS | `创建于 09/12/2026 00:08:54 之后 / 触发电量 < 20` | `2026-09-11 16:08:59.5627827+00:00 / 15` |
| FL2-CH-02 | 充电行程派了一条去充电桩 211 的 TO_CHARGER 单并已确认 | PASS | `TO_CHARGER / 211 / CONFIRMED` | `TO_CHARGER / 211 / CONFIRMED` |
| FL2-CH-03 | 车到桩并真的接上电：行程进入 Charging，驱动脚本全程没有看到 CHARGER_NOT_ENGAGED 或任何 CHARGER_* 阻塞 | PASS | `进入 Charging / 无 CHARGER_* 阻塞` | `AwaitingChargerArrival -> Charging -> Completed` |
| FL2-CH-04 | 充到恢复线释放：行程 Completed，释放电量 >= 80 | PASS | `Completed / >= 80` | `Completed / 80` |
| FL2-CH-05 | 释放之后接着受理下一单：下一趟旅程在充电行程结束之后创建，两趟之间只充了这一次电 | PASS | `有下一趟旅程 / 两趟之间 1 次充电` | `a1e6f039 创建于 2026-09-11 16:09:09.6238032+00:00 / 1 次` |
| FL2-R-00 | 至少观测到一次车静止时的服务重启（缺陷 20260908 的发现条件：真实服务重启加一台真车） | PASS | `>= 1` | `2` |
| FL2-R1-01 | 重启后新会话（generation > 重启前的 1）回到 Ready，checkpoint r1-ready 上的会话行同样是 Ready | PASS | `generation > 1 / Ready` | `1/Ready(READY) -> 2/Ready(READY) / checkpoint: 2/Ready` |
| FL2-R1-02 | 从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `0–60 秒` | `3.7 秒` |
| FL2-R1-03 | 重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `Completed` | `Completed` |
| FL2-R2-01 | 重启后新会话（generation > 重启前的 2）回到 Ready，checkpoint r2-ready 上的会话行同样是 Ready | PASS | `generation > 2 / Ready` | `2/Ready(READY) -> 3/Ready(READY) / checkpoint: 3/Ready` |
| FL2-R2-02 | 从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `0–60 秒` | `3.6 秒` |
| FL2-R2-03 | 重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `Completed` | `Completed` |
| FL2-W-01 | 现场记录由驱动脚本按实际动作写出，IO 是车上的 slots-simulator（无人到场） | PASS | `drivenBy 非空 / SIMULATOR` | `drivenBy=FieldOperator.psm1 run 20260911T160655450Z / SIMULATOR` |
| SC1-W-01 | 三个场景都有现场记录，且记录由驱动脚本按实际动作写出（模拟器 IO 下无人到场，照片不适用） | PASS | `3 个场景 / drivenBy 非空` | `6 个场景 / drivenBy=FieldOperator.psm1 run 20260911T160655450Z` |
| SC1-W-02 | 窗口内恢复入口是开着的——否则决策 2 只验证了一半 | FAIL | `True` | `False` |
| SC1-W-03 | 每一个车停着的 checkpoint 上会话都停在 Ready——全程没有把开着的仓门当成会话故障（行驶中的帧列出不判） | FAIL | `每个车停着的 checkpoint 都是 Ready` | `01-00-ready=Ready（行驶中 AwaitingPickupArrival，不判）; 02-t-settled=Ready; 03-x-settled=Ready; 04-ne-reopened=Ready; 05-r1-ready=Ready; 06-charge-dispatched=RecoveryRequired; 07-charge-charging=Ready; 08-charge-released=RecoveryRequired（行驶中 AwaitingPickupArrival，不判）; 09-r2-ready=Ready; 10-finalize=Ready` |

## 实测事实

不作判据，但下一次窗口和现场归因都要用：

| 项 | 值 |
| --- | --- |
| `ioKind` | `SIMULATOR` |
| `minimumHoldMinutes` | `20` |
| `drivenBy` | `FieldOperator.psm1 run 20260911T160655450Z` |
| `sublotWaitMinutes` | `1` |
| `journeyIds` | `bd52ba9b-d330-c55e-aed0-991b6e1c39e0, a1e6f039-8554-3857-891f-798645a35377` |
| `T.demand` | `49be257f-985c-4b1c-96a3-f9b86a3234b8 / L2-FW2-J1-N1-3-20260911T160655450Z / 停靠 1` |
| `X.demand` | `de7c3d70-ddfb-4305-9f42-685c3ad53136 / L2-FW2-J1-N2-6-20260911T160655450Z / 停靠 2` |
| `X.faceAnswer` | `HTTP 200` |
| `NE.attempt` | `7e282e65-9572-375b-82e2-aa1b5fa4d03a / 仓 3 / UNLOCKING=3 WAITING_OPERATOR=3` |
| `CH.thresholds` | `出厂 20/80` |
| `CH.seen` | `AwaitingChargerArrival -> Charging -> Completed` |
| `R1.series` | `1/Ready(READY) -> 2/Ready(READY)` |
| `R2.series` | `2/Ready(READY) -> 3/Ready(READY)` |

## 照片指针

照片本身不进 git，这里只留指针。

模拟器 IO 下无人到场，没有照片；现场记录由驱动脚本按实际动作写出。

## checkpoint 序列

场景 C 的关键值是「期限到期后再等 20 分钟依然不结束」，而结算本身会覆盖掉那一刻的状态——
所以它只能由当时抓下的 checkpoint 回答，不能事后从库里读。

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
- `snapshots/<序号>-<label>/` —— 每个 checkpoint 当时的库行、两端安装清单与车载端日志

**整个 `controlserver.db` 不在这里**。它是生产库，装着与本窗口无关的旅程，而 `evidence/` 进 git。
完整库落在 `C:\Users\szy\w2g-stage\field` 下的 `FW-FL2-<runId>`，需要时按 runId 找；
`run-demand-bearing-g3-vectors.ps1` 的 `-FieldRunRoot` 要的就是那个形状。

## 这份证据证明了什么，没证明什么

**证明了**：ADR-cross-0058 的决策 1、2、4、5 在 `AGV-L2-001`（L2 真装置彩排（本机））上、IO 接 slots-simulator
（`127.0.0.1:58412`）时，**软件闭环**的行为与判据一致；操作员的每一个动作由驱动脚本冒充，记录与 checkpoint 由它按实际动作写出。
车是不是真的在线路上走，这份证据不回答——现场窗口是，L2 彩排不是，看「现场」一栏。

**没有证明**：光幕极性、锁反馈时序与机械弹开行为。ADR-cross-0058 原话是模拟器证明不了这三样，而本窗口的 IO 正是模拟器——
这是 2026-09-11 改为全部无人值守时有意放弃的，见地图 Out of scope。

**没有证明**：完整闭环。受理→取货→录 SUBLOT→装货→安全检查→去关卡→卸货→收尾，以及自动充电与取消订单
两条支路，属于现场窗口二（[#20](https://github.com/trytoreachpeak0/8005-agv-program/issues/20)），不在这份证据里。

**没有证明**：另外两台车。本窗口只在 `AGV-L2-001` 上跑；三台上位机是同一个镜像的克隆，但 IO 接线是
逐车的，一台的绿说明不了另外两台。
