# L2 场景证据：g3-automatic-charging-cycle

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T033201863Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `3411887df3d9941a50e86c19eeba3ef928a714e9` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T033201863Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 低电：充电旅程是一趟没有需求的旅程，开往 211 的充电单已确认；途中计划（一条 CHARGER、ACTIVE 的腿）先于 activePurpose=CHARGING、EN_ROUTE 的业务状态进发件箱，两张都被真车载端确认（CV-AUTOMATIC-CHARGING-CYCLE 的消息顺序） | PASS | `无需求 / 计划先于业务状态、都已确认` | `demand ''/'' / plan r2 [1:CHARGER@充电点1:ACTIVE] ack=True @ 10/10/2026 03:32:32 +00:00 / business r2 purpose=CHARGING cycle=EN_ROUTE ack=True @ 10/10/2026 03:32:32 +00:00` |
| 到桩充电：211 转为这一趟的占用，周期 CHARGING，界面 ChargingStatus 报 CHARGING；车停在桩上那十秒里车载端每次读都不能提交，服务端也没有建任何装卸操作 | PASS | `CHARGING \| OCCUPIED charging:BROKERX-L2-0001:20261010T033231539Z \| CHARGING / 不能提交 / 0 笔操作` | `CHARGING \| OCCUPIED charging:BROKERX-L2-0001:20261010T033231539Z \| CHARGING / CanSubmit=False / 操作` |
| 充电中、低于完成阈值：甲发布之后十秒里一直没派给这辆车，周期一直是 CHARGING，用途一直是那一份 CHARGING（NEVER_DISPATCH_DURING_CHARGING） | PASS | `0 journeys \| CHARGING \| CHARGING charging:BROKERX-L2-0001:20261010T033231539Z` | `0 journeys \| CHARGING \| CHARGING charging:BROKERX-L2-0001:20261010T033231539Z` |
| 从分配到充满，这辆车一直只持有那一份 CHARGING 用途（分配时、充电中都是同一趟），充满时放开（CLAIM_VEHICLE_FOR_CHARGING_PURPOSE） | PASS | `CHARGING charging:BROKERX-L2-0001:20261010T033231539Z → COMPLETE \| (none)` | `CHARGING charging:BROKERX-L2-0001:20261010T033231539Z → COMPLETE \| (none)` |
| 充满：收尾计划仍是那一条 CHARGER 腿、ARRIVED，收尾业务状态 chargingCycleState=COMPLETE、activePurpose 为空，两张都被确认；界面 ChargingStatus 报 COMPLETE；211 仍是这一趟的占用 | PASS | `计划 [1:CHARGER@…:ARRIVED] 已确认 / 业务状态 COMPLETE、用途 null 已确认 / 界面 COMPLETE / OCCUPIED charging:BROKERX-L2-0001:20261010T033231539Z` | `plan r4 [1:CHARGER@充电点1:ARRIVED] ack=True / business r4 purpose=null cycle=COMPLETE ack=True / 界面 'COMPLETE' / OCCUPIED charging:BROKERX-L2-0001:20261010T033231539Z` |
| 甲派给充满的这辆车：下达那一刻 211 仍是充电那一趟的占用；被确认的最新一版计划属于甲、不含 CHARGER 腿；车到 12 号站车载端录入可用、甲装货提交；211 以 CHARGER_RELEASED_ON_DEPARTURE 释放、周期以 CHARGING_DEPARTED 收尾 | PASS | `OCCUPIED charging:BROKERX-L2-0001:20261010T033231539Z / 最新已确认计划属于甲、无充电腿 / load A Committed / (none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED CHARGING_DEPARTED` | `OCCUPIED charging:BROKERX-L2-0001:20261010T033231539Z / plan r5 [1:BUSINESS@N1-3_N1-7:ACTIVE,2:BUSINESS@关卡:PLANNED] ack=True / load A Committed / (none) \| CHARGER_RELEASED_ON_DEPARTURE \| ENDED CHARGING_DEPARTED` |
| 甲一次装、一次卸、都 Committed，需求 Succeeded，旅程 Completed；RIoT 上恰好一张充电单（没有重复的单）；装过的仓最后关门、空、锁上、开锁输出复位 | PASS | `Completed / A:Succeeded/ops 2/load 1/unload 1/charge orders 1 / 仓 CLOSED/EMPTY/1/0` | `Completed / A:Succeeded/ops 2/load 1/unload 1/charge orders 1 / 1=CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
