# 现场窗口二证据：完整闭环、取消订单、两趟之间自动充电、车静止时服务重启

**注意：本次按 `-SublotWaitMinutes 5` 判场景 T，且没有读到生产配置，只能算彩排，不能当现场窗口的证据。**



**本窗只欠场景 CH、N、R。**T、X、NE 不在本窗的剧本里，判据表里没有它们的行：既不算演到，也不算没演到。

结论：**PASS**

对应地图票 [现场窗口二](https://github.com/trytoreachpeak0/8005-agv-program/issues/20)。

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260912T095229543Z` |
| 窗口 | `FW-FL2` |
| 现场 | L2 真装置彩排（本机） |
| 现场人员 | 无人到场，FieldOperator.psm1 run 20260912T095122103Z |
| agvId | `AGV-L2-001` |
| IO | `127.0.0.1:58412`（SIMULATOR） |
| 站点期限 | 5 分钟 |
| 旅程 | `63b5d126-d8cd-4453-b673-6cef7301ca3e`（充电之后） |
| 数据库 | `local-snapshot` |

## 判据

| 判据 | 说明 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- | --- |
| FL2-N-01 | 窗口里的每一趟旅程都走到 Completed，派车租约都已释放 | PASS | `全部 Completed / 已释放` | `63b5d126=Completed/已释放` |
| FL2-N-02 | 完整闭环至少走通一次：一条需求录了 SUBLOT、装货提交、过出车前安全检查、卸货提交、需求 Succeeded（装货能提交即缺陷 20260829 现场未复现） | PASS | `至少一条需求全链路走通` | `2014a558: SUBLOT✓ 装Committed 安检✓ 卸Committed; 9677b2da: SUBLOT✓ 装Committed 安检✓ 卸Committed` |
| FL2-N-03 | 每一条 Succeeded 的需求都是全链路走通的——没有哪一条跳过了 SUBLOT、安全检查或卸货提交 | PASS | `2 条都走通` | `2 条走通` |
| FL2-CH-01 | 上一趟旅程结束之后才起充电行程，触发时电量低于当时生效的触发线 | PASS | `创建于 09/12/2026 17:51:41 之后 / 触发电量 < 30` | `2026-09-12 09:51:42.7562564+00:00 / 15` |
| FL2-CH-02 | 充电行程派了一条去充电桩 211 的 TO_CHARGER 单并已确认 | PASS | `TO_CHARGER / 211 / CONFIRMED` | `TO_CHARGER / 211 / CONFIRMED` |
| FL2-CH-03 | 车到桩并真的接上电：行程进入 Charging，驱动脚本全程没有看到 CHARGER_NOT_ENGAGED 或任何 CHARGER_* 阻塞 | PASS | `进入 Charging / 无 CHARGER_* 阻塞` | `AwaitingChargerArrival -> Charging -> Completed` |
| FL2-CH-04 | 充到恢复线释放：行程 Completed，释放电量 >= 80 | PASS | `Completed / >= 80` | `Completed / 80` |
| FL2-CH-05 | 释放之后接着受理下一单：下一趟旅程在充电行程结束之后创建，两趟之间只充了这一次电 | PASS | `有下一趟旅程 / 两趟之间 1 次充电` | `63b5d126 创建于 2026-09-12 09:51:56.8437399+00:00 / 1 次` |
| FL2-R-00 | 至少观测到一次车静止时的服务重启（缺陷 20260908 的发现条件：真实服务重启加一台真车） | PASS | `>= 1` | `1` |
| FL2-R1-01 | 重启后新会话（generation > 重启前的 1）回到 Ready，checkpoint r1-ready 上的会话行同样是 Ready | PASS | `generation > 1 / Ready` | `1/Ready(READY) -> 2/Ready(READY) / checkpoint: 2/Ready` |
| FL2-R1-02 | 从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `0–60 秒` | `7.2 秒` |
| FL2-R1-03 | 重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `Completed` | `Completed` |
| FL2-W-01 | 现场记录由驱动脚本按实际动作写出，IO 是车上的 slots-simulator（无人到场） | PASS | `drivenBy 非空 / SIMULATOR` | `drivenBy=FieldOperator.psm1 run 20260912T095122103Z / SIMULATOR` |

## 实测事实

| 项 | 值 |
| --- | --- |
| `ioKind` | `SIMULATOR` |
| `minimumHoldMinutes` | `20` |
| `drivenBy` | `FieldOperator.psm1 run 20260912T095122103Z` |
| `sublotWaitMinutes` | `5` |
| `journeyIds` | `63b5d126-d8cd-4453-b673-6cef7301ca3e` |
| `scenesOwed` | `CH, N, R` |
| `CH.thresholds` | `出厂 30/80` |
| `CH.seen` | `AwaitingChargerArrival -> Charging -> Completed` |
| `R1.series` | `1/Ready(READY) -> 2/Ready(READY)` |

## checkpoint 序列

场景 NE 的「重开几轮之后仍在进行」与场景 R 的「重启之后会话 Ready」都是会被后面的写入覆盖掉的时刻，
只能由当时抓下的 checkpoint 回答。

- `snapshots/01-00-ready/`
- `snapshots/02-charge-dispatched/`
- `snapshots/03-charge-charging/`
- `snapshots/04-charge-released/`
- `snapshots/05-r1-ready/`
- `snapshots/06-finalize/`

## 目录内容

- `assertions.json` —— 机器可读的判据结论，含现场记录原文与实测事实
- `timeline.jsonl` —— 一行一次 checkpoint，只追加
- `logs/` —— 每次远端拷贝的输出
- `snapshots/<序号>-<label>/` —— 每个 checkpoint 当时的库行、两端安装清单、生产配置里的门与充电线、车载端日志

**整个 `controlserver.db` 不在这里**。它是生产库，完整副本落在 `C:\Users\szy\w2g-stage\field` 下的 `FW-FL2-<runId>`。

## 这份证据证明了什么，没证明什么

**证明了**：在 `AGV-L2-001`（L2 真装置彩排（本机））上、车走真实线路、IO 接 slots-simulator（`127.0.0.1:58412`）时，
一趟 WIRE_TO_GATE 旅程从受理到关卡收尾的软件闭环；没有未完成旅程时车会自己去 211 充电、到恢复线后接单；车静止时服务重启后会话自己回到 Ready。

**没有证明**：

- **出厂触发线与恢复线（30% / 80%）这两个数本身。**充电那一幕按用户 2026-09-11 的决定临时抬线（见 出厂 30/80），证的是机制，不是出厂阈值；出厂值只由 L2 `auto-charge-endurance` 覆盖。
- **光幕极性、锁反馈时序与机械弹开。**IO 是模拟器，与现场窗口一（无人）同一个缺口，见地图 Out of scope。
- **另外两台车。**
