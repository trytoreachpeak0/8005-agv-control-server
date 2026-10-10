# L2 场景证据：g3-waiting-point-idle-return

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T033033567Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T033033567Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车空闲、没有需求：空闲返回是一趟没有需求的旅程，开往 214 的单段移动已确认、214 是它的在途预占；途中计划（一条 WAITING_POINT、ACTIVE 的腿）先于 activePurpose=IDLE_RETURN 的业务状态进发件箱，两张都被真车载端确认（CV-WAITING-POINT-IDLE-RETURN 的消息顺序） | PASS | `无需求 / 214 RESERVED idle-return:BROKERX-L2-0001:20261010T033106357Z / 计划先于业务状态、都已确认` | `demand ''/'' / RESERVED WAITING_POINT idle-return:BROKERX-L2-0001:20261010T033106357Z / plan r1 [1:WAITING_POINT@等待点1:ACTIVE] ack=True @ 10/10/2026 03:31:07 +00:00 / business r1 purpose=IDLE_RETURN ack=True @ 10/10/2026 03:31:07 +00:00` |
| 车载端确认了途中两张之后，到站那一格报 EN_ROUTE_TO_WAITING_POINT（TREAT_WAITING_POINT_AS_NON_BUSINESS_STOP） | PASS | `EN_ROUTE_TO_WAITING_POINT` | `'EN_ROUTE_TO_WAITING_POINT'` |
| 车停在等待点上：车载端不开放录入（十秒里每次读都不能提交），服务端也没有建任何装卸操作——此刻一条需求都还没有（NEVER_LOAD_AT_WAITING_POINT） | PASS | `界面 AT_WAITING_POINT、不能提交 / 0 笔操作` | `界面 'AT_WAITING_POINT'、CanSubmit=False / 操作` |
| 到点收敛：214 转为这一趟的在点占用、IDLE_RETURN 用途释放；收尾那张计划仍是那一条等待点腿、状态 ARRIVED（不标 COMPLETED、不删），收尾那张业务状态的 activePurpose 不是 IDLE_RETURN，两张都被确认；界面报 AT_WAITING_POINT，并在其后十秒里一直是 | PASS | `Completed / 214 OCCUPIED / 用途 IDLE_RETURN_CONVERGED_AT_WAITING_POINT / 计划 [1:WAITING_POINT@…:ARRIVED] 已确认 / 业务状态非 IDLE_RETURN 已确认 / 界面 AT_WAITING_POINT 十秒不变` | `Completed  / OCCUPIED WAITING_POINT idle-return:BROKERX-L2-0001:20261010T033106357Z / 用途 IDLE_RETURN_CONVERGED_AT_WAITING_POINT / plan r2 [1:WAITING_POINT@等待点1:ARRIVED] ack=True / business r2 purpose=null ack=True / 界面 'AT_WAITING_POINT' → 'AT_WAITING_POINT'` |
| 甲把车派走：被确认的最新一版计划是甲那一趟的，不含等待点腿（不留 ARRIVED 的等待点腿、不排在业务腿前面）；界面到站那一格不再报 EN_ROUTE_TO_WAITING_POINT／AT_WAITING_POINT | PASS | `最新已确认计划属于甲、无等待点腿 / 界面不报空闲返回` | `plan r4 [1:BUSINESS@N1-3_N1-7:ACTIVE,2:BUSINESS@关卡:PLANNED] ack=True / 界面 ''` |
| 空闲返回释放后接新旅程：车到 12 号站，车载端录入可用、甲的装货提交；214 的独占以 DEPARTED_STATION 释放 | PASS | `load A Committed / 214 released DEPARTED_STATION` | `load A Committed / held (none) / record DEPARTED_STATION` |
| 甲一次装、一次卸、都 Committed，需求 Succeeded，旅程 Completed；装过的仓最后关门、空、锁上、开锁输出复位 | PASS | `Completed / A:Succeeded/ops 2/load 1/unload 1 / 仓 CLOSED/EMPTY/1/0` | `Completed / A:Succeeded/ops 2/load 1/unload 1 / 1=CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
