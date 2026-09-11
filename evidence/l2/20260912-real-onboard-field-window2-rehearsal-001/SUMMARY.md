# L2 场景证据：real-onboard-field-window2-rehearsal

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T160655450Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `9e7db453227b3bae420525cc8b5c6c364a1f58d9` |
| onboardHmiCommit | `6b8a0b06575f40fa0e682c5d307b9ce46bbe9227` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T160655450Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四条需求凑成第一趟旅程：四个取货停靠加一个关卡 | PASS | `5` | `5` |
| 停靠 1 没人扫码：站点期限到期后需求被结算并抑制，旅程自己离站 | PASS | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 离站` | `Cancelled / CANCELLED_BY_STATION_TIMEOUT / 1/AwaitingDepartureSafety` |
| 停靠 2 扫码前取消：需求 Cancelled 并以 CANCELLED_BY_OPERATOR 抑制，旅程自己离站 | PASS | `Cancelled / CANCELLED_BY_OPERATOR / 离站` | `Cancelled / CANCELLED_BY_OPERATOR / 2/AwaitingDepartureSafety（面答 HTTP 200）` |
| 第一趟停靠 3：照常装载提交 | PASS | `Committed` | `Committed` |
| 第一趟停靠 4：照常装载提交 | PASS | `Committed` | `Committed` |
| 第一趟关卡：第一个开的仓关门不取空两轮都换来重开，之后两条卸货提交、旅程 Completed | PASS | `2 轮 / 2 条 Committed` | `2 轮 / Committed,Committed` |
| 充电未到恢复线时不受理第二趟的需求 | PASS | `(无未完成旅程)` | `(无未完成旅程)` |
| 释放之后接着受理第二趟 | PASS | `新旅程` | `a1e6f039-8554-3857-891f-798645a35377` |
| 第二趟停靠 1：照常装载提交 | PASS | `Committed` | `Committed` |
| 第二趟停靠 2：照常装载提交 | PASS | `Committed` | `Committed` |
| 第二趟关卡：两条卸货提交、旅程 Completed | PASS | `2 条 Committed` | `Committed,Committed` |
| 采集器：到站不录入 SUBLOT：需求被服务端终结为 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_STATION_TIMEOUT 永久抑制 | PASS | `"Cancelled / CANCELLED_BY_STATION_TIMEOUT / 抑制键 = 需求键"` | `"Cancelled / CANCELLED_BY_STATION_TIMEOUT / 键一致"` |
| 采集器：终结之前一次仓位操作都没有下发——没人扫码就没有装货 | PASS | `0` | `0` |
| 采集器：站点期限是 1 分钟：车载端拿到的期限距驱动脚本看到服务端开始等 SUBLOT 不超过 1 分钟、且不短于它 60 秒以上 | PASS | `"0–60 秒"` | `"59.4 秒"` |
| 采集器：到期才结算、没有无限拖着：结算被看到的时刻落在期限之后 0–180 秒 | PASS | `"0–180 秒"` | `"1.2 秒"` |
| 采集器：车被释放：结算后旅程自己离开这一站，这趟旅程最终 Completed、派车租约已释放——没有停在 AwaitingSublot | PASS | `"离站 / Completed / 租约已释放"` | `"1/AwaitingDepartureSafety / Completed / 已释放"` |
| 采集器：到站还没装货就取消：需求 Cancelled，并按它的 TransportDemandKey 以 CANCELLED_BY_OPERATOR 永久抑制（判的是服务端库，不是自动化面的返回） | PASS | `"Cancelled / CANCELLED_BY_OPERATOR / 抑制键 = 需求键"` | `"Cancelled / CANCELLED_BY_OPERATOR"` |
| 采集器：取消发生在装货之前：这条需求没有任何仓位操作 | PASS | `0` | `0` |
| 采集器：取消用的是车载端界面那一刻真的给出的按钮（availableRecoveryActions 含 LOAD_CANCELLATION），之后旅程自己离开这一站 | PASS | `"LOAD_CANCELLATION 在按钮里 / 离站"` | `"LOAD_CANCELLATION / 2/AwaitingDepartureSafety"` |
| 采集器：关门时货还在：车载端自己把这一仓重开了 2 轮（该仓 UNLOCKING 至少 3 次） | PASS | `">= 3"` | `3` |
| 采集器：重开 2 轮之后卸货仍在进行（checkpoint ne-reopened）：操作 Prepared、旅程停在 AwaitingUnloadResult、需求没有被取消 | PASS | `"Prepared / AwaitingUnloadResult / 需求未取消"` | `"Prepared / AwaitingUnloadResult / Accepted"` |
| 采集器：唯一的出口是取空：卸货 Committed，这个 attempt 只有 COMPLETED 结果，需求 Succeeded | PASS | `"Committed / 仅 COMPLETED / Succeeded"` | `"Committed / COMPLETED / Succeeded"` |
| 采集器：窗口里的每一趟旅程都走到 Completed，派车租约都已释放 | PASS | `"全部 Completed / 已释放"` | `"bd52ba9b=Completed/已释放; a1e6f039=Completed/已释放"` |
| 采集器：完整闭环至少走通一次：一条需求录了 SUBLOT、装货提交、过出车前安全检查、卸货提交、需求 Succeeded（装货能提交即缺陷 20260829 现场未复现） | PASS | `"至少一条需求全链路走通"` | `"0848a67f: SUBLOT✓ 装Committed 安检✓ 卸Committed; 1834a85c: SUBLOT✓ 装Committed 安检✓ 卸Committed; 61b1b152: SUBLOT✓ 装Committed 安检✓ 卸Committed; f6e1dd1d: SUBLOT✓ 装Committed 安检✓ 卸Committed"` |
| 采集器：每一条 Succeeded 的需求都是全链路走通的——没有哪一条跳过了 SUBLOT、安全检查或卸货提交 | PASS | `"4 条都走通"` | `"4 条走通"` |
| 采集器：上一趟旅程结束之后才起充电行程，触发时电量低于当时生效的触发线 | PASS | `"创建于 09/12/2026 00:08:54 之后 / 触发电量 < 20"` | `"2026-09-11 16:08:59.5627827+00:00 / 15"` |
| 采集器：充电行程派了一条去充电桩 211 的 TO_CHARGER 单并已确认 | PASS | `"TO_CHARGER / 211 / CONFIRMED"` | `"TO_CHARGER / 211 / CONFIRMED"` |
| 采集器：车到桩并真的接上电：行程进入 Charging，驱动脚本全程没有看到 CHARGER_NOT_ENGAGED 或任何 CHARGER_* 阻塞 | PASS | `"进入 Charging / 无 CHARGER_* 阻塞"` | `"AwaitingChargerArrival -> Charging -> Completed"` |
| 采集器：充到恢复线释放：行程 Completed，释放电量 >= 80 | PASS | `"Completed / >= 80"` | `"Completed / 80"` |
| 采集器：释放之后接着受理下一单：下一趟旅程在充电行程结束之后创建，两趟之间只充了这一次电 | PASS | `"有下一趟旅程 / 两趟之间 1 次充电"` | `"a1e6f039 创建于 2026-09-11 16:09:09.6238032+00:00 / 1 次"` |
| 采集器：至少观测到一次车静止时的服务重启（缺陷 20260908 的发现条件：真实服务重启加一台真车） | PASS | `">= 1"` | `2` |
| 采集器：重启后新会话（generation > 重启前的 1）回到 Ready，checkpoint r1-ready 上的会话行同样是 Ready | PASS | `"generation > 1 / Ready"` | `"1/Ready(READY) -> 2/Ready(READY) / checkpoint: 2/Ready"` |
| 采集器：从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `"0–60 秒"` | `"3.7 秒"` |
| 采集器：重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `"Completed"` | `"Completed"` |
| 采集器：重启后新会话（generation > 重启前的 2）回到 Ready，checkpoint r2-ready 上的会话行同样是 Ready | PASS | `"generation > 2 / Ready"` | `"2/Ready(READY) -> 3/Ready(READY) / checkpoint: 3/Ready"` |
| 采集器：从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `"0–60 秒"` | `"3.6 秒"` |
| 采集器：重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `"Completed"` | `"Completed"` |
| 采集器：现场记录由驱动脚本按实际动作写出，IO 是车上的 slots-simulator（无人到场） | PASS | `"drivenBy 非空 / SIMULATOR"` | `"drivenBy=FieldOperator.psm1 run 20260911T160655450Z / SIMULATOR"` |
| 采集器：三个场景都有现场记录，且记录由驱动脚本按实际动作写出（模拟器 IO 下无人到场，照片不适用） | PASS | `"3 个场景 / drivenBy 非空"` | `"6 个场景 / drivenBy=FieldOperator.psm1 run 20260911T160655450Z"` |
| 采集器：窗口内恢复入口是开着的——否则决策 2 只验证了一半 | FAIL | `true` | `false` |
| 采集器：每一个车停着的 checkpoint 上会话都停在 Ready——全程没有把开着的仓门当成会话故障（行驶中的帧列出不判） | FAIL | `"每个车停着的 checkpoint 都是 Ready"` | `"01-00-ready=Ready（行驶中 AwaitingPickupArrival，不判）; 02-t-settled=Ready; 03-x-settled=Ready; 04-ne-reopened=Ready; 05-r1-ready=Ready; 06-charge-dispatched=RecoveryRequired; 07-charge-charging=Ready; 08-charge-released=RecoveryRequired（行驶中 AwaitingPickupArrival，不判）; 09-r2-ready=Ready; 10-finalize=Ready"` |
| 采集器在驱动写出的记录上 finalize，整窗 PASS | FAIL | `exit 0 / PASS` | `exit 1 / FAIL` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 177 lines, 177 distinct, 12 message types, 0 distinct violations, 0 known; schema compilation 24006 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
