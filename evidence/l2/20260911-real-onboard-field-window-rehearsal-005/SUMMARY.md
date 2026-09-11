# L2 场景证据：real-onboard-field-window-rehearsal

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260911T063706977Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `12988ac49d7424059bfd449ff388c33d9a91cd1b` |
| onboardHmiCommit | `bb58b217e95c611de597212d763f7579fdcb0e03` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260911T063706977Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四条需求凑成一趟旅程：四个取货停靠加一个关卡 | PASS | `5` | `5` |
| 停靠 1 场景 A：驱动脚本空关两轮都换来车自己重开，之后放料提交，恢复入口一次没出现 | PASS | `2 轮 / Committed / 入口未出现 / UNLOCKING >= 3` | `2 轮 / Committed / 入口未出现（看到过的按钮：LOAD_CANCELLATION） / UNLOCKING 3` |
| 停靠 2 场景 C 接 B：门开着过期挂告警并撑满 3 分钟，回来空关后结算成 Failed，旅程自己离站 | PASS | `Failed / 离开 2/AwaitingLoadResult` | `Failed / 3/AwaitingPickupArrival（空关 2 次，重开 1 次）` |
| 停靠 3：驱动脚本照常装载提交 | PASS | `Committed` | `Committed` |
| 停靠 4：驱动脚本照常装载提交 | PASS | `Committed` | `Committed` |
| 采集器：开门不放料：车载端自动重发开锁脉冲，轮次不设上限（现场至少走到第 3 轮） | PASS | `"UNLOCKING >= 3"` | `3` |
| 采集器：重开几轮之后照常放料，装载提交——不设上限的重开有出口 | PASS | `"Committed"` | `"Committed"` |
| 采集器：人没放料没有被判成失败：这个 attempt 上没有 FAILED 结果 | PASS | `0` | `0` |
| 采集器：需求没有被判 RecoveryRequired | PASS | `"不是 RecoveryRequired"` | `"Accepted"` |
| 采集器：HMI 上没有出现恢复入口——操作员迟疑不需要管理员凭据（驱动脚本读车载端快照 availableRecoveryActions 里的三个恢复动作） | PASS | `false` | `false` |
| 采集器：车载端报 FAILED | PASS | `"FAILED"` | `"FAILED"` |
| 采集器：三个物理字段都是明确的：State 不是 Unknown、锁已闭、开锁输出已复位 | PASS | `"三字段齐全且明确"` | `"slot2:State=Empty/DoorLocked=True/UnlockOutputReset=True"` |
| 采集器：服务端走确定失败而不是 RecoveryRequired | PASS | `"Failed"` | `"Failed"` |
| 采集器：确定失败之后服务端自己终结这条需求：Cancelled、按 CANCELLED_BY_STATION_TIMEOUT 永久抑制、装货命令已结算 | PASS | `"Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled"` | `"Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled"` |
| 采集器：确定失败之后旅程自己离开这一格，不需要任何人按任何按钮 | PASS | `"不是 2/AwaitingLoadResult"` | `"9/AwaitingGateArrival"` |
| 采集器：结算之后会话仍在 Ready：确定失败是业务结果，不是会话故障 | PASS | `"Ready"` | `"Ready / READY"` |
| 采集器：站点期限到期时告警挂上了 STATION_TIMEOUT_DOOR_NOT_CLOSED | PASS | `"STATION_TIMEOUT_DOOR_NOT_CLOSED"` | `"STATION_TIMEOUT_DOOR_NOT_CLOSED"` |
| 采集器：期限到期时停靠没有被关闭，stage 停在 AwaitingLoadResult 而不是 Blocked | PASS | `"AwaitingLoadResult"` | `"AwaitingLoadResult"` |
| 采集器：期限到期后再等 3 分钟依然不结束——等待不会自己退化成结束 | PASS | `"AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED / Prepared"` | `"AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED / Prepared"` |
| 采集器：那次复查确实在期限之后 3 分钟以上 | PASS | `">= 3 分钟"` | `"3.0 分钟"` |
| 采集器：门一直开着：晾过 OperationTimeout 之后提示还在走而脉冲只打过一次——提示节拍不重复开一把已经开着的锁 | PASS | `"UNLOCKING = 1 / WAITING_OPERATOR >= 2"` | `"UNLOCKING 1 / WAITING_OPERATOR 2"` |
| 采集器：关门之后按真实 IO 读数结算（决策 1 会先重开一轮），告警随之消失 | PASS | `"告警清空 / 操作已结算"` | `"AwaitingGateArrival / - / Failed"` |
| 采集器：三个场景都有现场记录，且记录由驱动脚本按实际动作写出（模拟器 IO 下无人到场，照片不适用） | PASS | `"3 个场景 / drivenBy 非空"` | `"3 个场景 / drivenBy=FieldOperator.psm1 run 20260911T063706977Z"` |
| 采集器：窗口内恢复入口是开着的——否则决策 2 只验证了一半 | PASS | `true` | `true` |
| 采集器：每一个 checkpoint 上会话都停在 Ready——全程没有把开着的仓门当成会话故障 | PASS | `"每个 checkpoint 都是 Ready"` | `"01-00-ready=Ready; 02-c-deadline-reached=Ready; 03-c-plus-hold=Ready; 04-b-settled=Ready; 05-finalize=Ready"` |
| 采集器在驱动脚本写出的记录上 finalize，整窗 PASS | PASS | `exit 0 / PASS` | `exit 0 / PASS` |
| 关卡：驱动脚本把三条装上车的需求逐条取空，每条卸货都提交，旅程 Completed（多需求关卡清单被车载端拒收时红，见 8005-agv-program#48） | FAIL | `Completed / 3 条卸货 Committed` | `AwaitingUnloadResult /  / 会话 RecoveryRequired/HANDSHAKE_INCOMPLETE / Timed out after 300s unloading journey ad47b974-4757-6a53-b70f-a18e68c25923; stage AwaitingUnloadResult.` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（真车载端包写进 `ProtocolInbox.RequestJson` 的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 370 lines, 370 distinct, 12 message types, 0 distinct violations, 0 known; schema compilation 19924 ms.` |

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
