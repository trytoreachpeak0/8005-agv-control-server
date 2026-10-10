# L2 场景证据：g3-reversed-direction-journey

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T050116011Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `1f63fe0bb173f37c05dd5059c78455da57ed100b` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T050116011Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 计划按任务类型规则排腿：第一腿 TO_PICKUP 到派工待送站、第二腿 TO_DROPOFF 到 AREA 机台，publicStationFunction 为空（DERIVE_DIRECTION_FROM_TASK_TYPE_RULE；车出发前判） | PASS | `1:TO_PICKUP@派工待送,2:TO_DROPOFF@N1-3_N1-7 / publicStationFunction 非空 0 条` | `1:TO_PICKUP@派工待送,2:TO_DROPOFF@N1-3_N1-7 / publicStationFunction 非空 0 条` |
| 起终点没有互换：旅程记下的起点是派工待送站、终点是 AREA 机台，只有一行；路线证据等于按计划方向独立重算出来的那个 id，而把两端互换重算会得到另一个 id；RIoT 上第一张单开往派工待送站（NEVER_SWAP_ORIGIN_AND_DESTINATION；车出发前判，第二张单的目的站在 G3-11-06） | PASS | `派工待送/230 → N1-3_N1-7/12 / 路线证据 = 正向重算 MAPCAT-17825775b01077…、≠ 反向重算 MAPCAT-01f60412c4f8da… / 第一张单 → 230` | `派工待送/230 → N1-3_N1-7/12 / 路线证据 MAPCAT-17825775b01077… / 第一张单 → 230` |
| 清单的 stopRole 跟着计划走：派工待送站那份是 PICKUP、AREA 机台那份是 DROPOFF，任务类型都是 STAGING_TO_WIRE | PASS | `派工待送 PICKUP/STAGING_TO_WIRE / N1-3_N1-7 DROPOFF/STAGING_TO_WIRE` | `派工待送 PICKUP/STAGING_TO_WIRE / N1-3_N1-7 DROPOFF/STAGING_TO_WIRE` |
| 消息顺序与向量一致：先发计划、被确认，之后才发清单、被确认；这一趟的计划与清单全部被真车载端确认，没有一份被作废 | PASS | `计划在前 / 全部 ack、无作废` | `plan r1 TO_PICKUP@派工待送:ACTIVE,TO_DROPOFF@N1-3_N1-7:PLANNED ack=True fenced=False \| worklist r1 @派工待送 PICKUP/STAGING_TO_WIRE ack=True fenced=False \| plan r2 TO_PICKUP@派工待送:ARRIVED,TO_DROPOFF@N1-3_N1-7:PLANNED ack=True fenced=False \| worklist r2 @N1-3_N1-7 DROPOFF/STAGING_TO_WIRE ack=True fenced=False \| plan r3 TO_PICKUP@派工待送:COMPLETED,TO_DROPOFF@N1-3_N1-7:ARRIVED ack=True fenced=False` |
| 车载端在派工待送站显示「取货」：StopDirection 含「取」、不含「卸」，TaskType 已显示（DISPLAY_DIRECTION_AS_PLANNED；不比文案全文） | PASS | `StopDirection 含「取」 / TaskType 非空` | `StopDirection=「取货」 TaskType=「待送→焊线机台」` |
| 车载端在 AREA 机台显示「卸货」：StopDirection 含「卸」、不含「取」，TaskType 已显示（DISPLAY_DIRECTION_AS_PLANNED；不按任务类型推） | PASS | `StopDirection 含「卸」 / TaskType 非空` | `StopDirection=「卸货」 TaskType=「待送→焊线机台」` |
| 装货在派工待送站、卸货在 AREA 机台：录入请求的站点是派工待送站，装货命令在车到派工待送站之后、出发去机台之前发出，第二张 RIoT 单开往 AREA 机台，卸货命令在车到机台之后发出；两条命令的 slots 都是目标仓，模拟器上装与卸开的是同一个目标仓 | PASS | `录入@派工待送 / 装 staging / 第二张单 → 12 / 卸 area / slots 1 / 开仓相同` | `录入@派工待送 / 装 staging / 第二张单 → 12 / 卸 area / 装 slots 1 卸 slots 1 目标 1 / 开仓 装 1 卸 1` |
| 站点任务类型准入冻结在卸货那次操作上：AdmissionDecisionSnapshots 里卸货那次有一行（AREA 机台站、STAGING_TO_WIRE、放行），装货那次没有（推翻 I6，准入跟着 AREA 端那条腿走） | PASS | `卸货 1 行 N1-3_N1-7/STAGING_TO_WIRE/放行 / 装货 0 行` | `卸货 1 行 N1-3_N1-7/STAGING_TO_WIRE/Allowed=1 / 装货 0 行` |
| 终态 NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE：需求 Succeeded、旅程 Completed，两张 RIoT 单、两笔仓位操作都 Committed，卸完的仓关门、空、锁上、开锁输出复位 | PASS | `Succeeded / Completed / 2 单 / 2 操作 2 Committed / CLOSED/EMPTY/1/0` | `Succeeded / Completed / 2 单 / 2 操作 2 Committed / 仓 1 CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
