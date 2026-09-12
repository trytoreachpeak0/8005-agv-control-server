# L2 场景证据：real-onboard-field-charging-window-rehearsal

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260912T095122103Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `4bd0db5408ca427b9ddc54cee0d9668611b809af` |
| onboardHmiCommit | `6b8a0b06575f40fa0e682c5d307b9ce46bbe9227` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260912T095122103Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 开窗时没有未完成旅程，充电幕不是插在两趟之间而是开窗第一幕 | PASS | `(无未完成旅程)` | `(无未完成旅程)` |
| 充电单完成后假 RIoT 自己报 CHARGING（单里带开始充电动作） | PASS | `CHARGING` | `CHARGING` |
| 充电未到恢复线时不受理需求 | PASS | `(无未完成旅程)` | `(无未完成旅程)` |
| 停靠 1：照常装载提交 | PASS | `Committed` | `Committed` |
| 停靠 2：照常装载提交 | PASS | `Committed` | `Committed` |
| 关卡：两条卸货提交、旅程 Completed | PASS | `2 条 Committed` | `Committed,Committed` |
| 采集器：窗口里的每一趟旅程都走到 Completed，派车租约都已释放 | PASS | `"全部 Completed / 已释放"` | `"63b5d126=Completed/已释放"` |
| 采集器：完整闭环至少走通一次：一条需求录了 SUBLOT、装货提交、过出车前安全检查、卸货提交、需求 Succeeded（装货能提交即缺陷 20260829 现场未复现） | PASS | `"至少一条需求全链路走通"` | `"2014a558: SUBLOT✓ 装Committed 安检✓ 卸Committed; 9677b2da: SUBLOT✓ 装Committed 安检✓ 卸Committed"` |
| 采集器：每一条 Succeeded 的需求都是全链路走通的——没有哪一条跳过了 SUBLOT、安全检查或卸货提交 | PASS | `"2 条都走通"` | `"2 条走通"` |
| 采集器：上一趟旅程结束之后才起充电行程，触发时电量低于当时生效的触发线 | PASS | `"创建于 09/12/2026 17:51:41 之后 / 触发电量 < 30"` | `"2026-09-12 09:51:42.7562564+00:00 / 15"` |
| 采集器：充电行程派了一条去充电桩 211 的 TO_CHARGER 单并已确认 | PASS | `"TO_CHARGER / 211 / CONFIRMED"` | `"TO_CHARGER / 211 / CONFIRMED"` |
| 采集器：车到桩并真的接上电：行程进入 Charging，驱动脚本全程没有看到 CHARGER_NOT_ENGAGED 或任何 CHARGER_* 阻塞 | PASS | `"进入 Charging / 无 CHARGER_* 阻塞"` | `"AwaitingChargerArrival -> Charging -> Completed"` |
| 采集器：充到恢复线释放：行程 Completed，释放电量 >= 80 | PASS | `"Completed / >= 80"` | `"Completed / 80"` |
| 采集器：释放之后接着受理下一单：下一趟旅程在充电行程结束之后创建，两趟之间只充了这一次电 | PASS | `"有下一趟旅程 / 两趟之间 1 次充电"` | `"63b5d126 创建于 2026-09-12 09:51:56.8437399+00:00 / 1 次"` |
| 采集器：至少观测到一次车静止时的服务重启（缺陷 20260908 的发现条件：真实服务重启加一台真车） | PASS | `">= 1"` | `1` |
| 采集器：重启后新会话（generation > 重启前的 1）回到 Ready，checkpoint r1-ready 上的会话行同样是 Ready | PASS | `"generation > 1 / Ready"` | `"1/Ready(READY) -> 2/Ready(READY) / checkpoint: 2/Ready"` |
| 采集器：从服务进程启动到会话 Ready 不超过 60 秒——缺陷现场是 6 分 36 秒不自愈，直到有人重启车载客户端 | PASS | `"0–60 秒"` | `"7.2 秒"` |
| 采集器：重启时车是静止的：重启前那趟旅程已经 Completed，这正是缺陷的发现条件 | PASS | `"Completed"` | `"Completed"` |
| 采集器：现场记录由驱动脚本按实际动作写出，IO 是车上的 slots-simulator（无人到场） | PASS | `"drivenBy 非空 / SIMULATOR"` | `"drivenBy=FieldOperator.psm1 run 20260912T095122103Z / SIMULATOR"` |
| 本窗不欠的 T、X、NE 在采集器判据表里一行都没有——既不算演到，也不算没演到 | PASS | `(无)` | `(无)` |
| 本窗欠的 CH 判据一条不少：FL2-CH-01..05 都在判据表里（CH-06 只在读到生产配置时判） | PASS | `(无缺失)` | `(无缺失)` |
| 采集器在充电短窗口的记录上 finalize，整窗 PASS | PASS | `exit 0 / PASS` | `exit 0 / PASS` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 79 lines, 79 distinct, 11 message types, 0 distinct violations, 0 known; schema compilation 16838 ms.` |

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
