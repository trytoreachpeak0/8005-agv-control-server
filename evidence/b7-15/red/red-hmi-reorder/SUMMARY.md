# L2 场景证据：g3-multi-stop-plan

结论：**FAIL**

失败原因：The property 'Legs' cannot be found on this object. Verify that the property exists.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T184050411Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `6bf3ee1653b290de66890b32791f70d6faa071f5` |
| onboardHmiCommit | `a7abe3aab0106a592f88340a53009b8ab6119d9b` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T184050411Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 追加之前：车载端确认了派车那一版计划之后，计划腿列表的行数与行序等于服务端那一版的 sequence（DISPLAY_FULL_JOURNEY_PLAN） | FAIL | `两条腿，行序 (无计划)` | `服务端 (无已确认计划) / 界面 (no rows)` |
| 追加之后：车载端确认了三条腿那一版计划之后，计划腿列表的行数与行序等于服务端那一版的 sequence；站点刻意挑成按站名排会是 2,1,3，本地重排就红（DISPLAY_FULL_JOURNEY_PLAN、NEVER_REORDER_LEGS_LOCALLY） | FAIL | `行序 1,2,3` | `服务端 plan r3 wire[1,2,3] 1:TO_PICKUP@N1-3_N1-7:ARRIVED:BUSINESS,2:TO_PICKUP@C15-13:PLANNED:BUSINESS,3:TO_DROPOFF@关卡:PLANNED:BUSINESS / 界面 2[2\|BUSINESS\|PLANNED],1[1\|BUSINESS\|ARRIVED],3[3\|BUSINESS\|PLANNED]` |
| 这趟旅程的每一版计划：腿的 sequence 从 1 起连续、无重复，每条腿都有 stopPurposeCategory（CATEGORISE_EVERY_STOP_PURPOSE） | PASS | `至少两版计划，全部 1..n 连续且每腿有用途类别` | `7 版计划，不合格 0 版` |
| 追加之后的计划修订号大于此前每一版，腿数不少于 3、不多于 9；乙进的是同一趟旅程（归属两条、旅程行一行）（PLAN_UP_TO_NINE_LEGS 的 G3 面，九条由两端 G2 证） | PASS | `修订号 > 此前最大 / 3..9 条腿 / 归属 2 / 旅程行 1` | `r3 对此前最大 r2 / 3 条腿 / 归属 2 / 旅程行 1` |
| 线上计划按 sequence 下发：每一版 UpcomingStopPlanSnapshot 的 legs 数组，原文的先后就是 sequence 从小到大（读发件箱原文，不排序）（ORDER_LEGS_BY_SEQUENCE） | PASS | `每一版原文升序` | `7 版，乱序 0 版` |
| 消息顺序与向量一致：这趟旅程第一份快照是计划，清单在它之后；每一份计划与清单都被真车载端确认（SnapshotAppliedAck）；作废过的只能是后面有同一流更高号一份的那种 | PASS | `计划在前 / 全部确认 / 没有无后继的作废` | `plan r1 wire[1,2] 1:TO_PICKUP@N1-3_N1-7:ACTIVE:BUSINESS,2:TO_DROPOFF@关卡:PLANNED:BUSINESS ack=True fenced=False \| worklist r1 @N1-3_N1-7 G3-08-A-20260921T184050411Z/PICKUP ack=True fenced=False \| plan r2 wire[1,2] 1:TO_PICKUP@N1-3_N1-7:ARRIVED:BUSINESS,2:TO_DROPOFF@关卡:PLANNED:BUSINESS ack=True fenced=False \| plan r3 wire[1,2,3] 1:TO_PICKUP@N1-3_N1-7:ARRIVED:BUSINESS,2:TO_PICKUP@C15-13:PLANNED:BUSINESS,3:TO_DROPOFF@关卡:PLANNED:BUSINESS ack=True fenced=False \| plan r4 wire[1,2,3] 1:TO_PICKUP@N1-3_N1-7:COMPLETED:BUSINESS,2:TO_PICKUP@C15-13:ACTIVE:BUSINESS,3:TO_DROPOFF@关卡:PLANNED:BUSINESS ack=True fenced=False \| worklist r2 @C15-13 G3-08-B-20260921T184050411Z/PICKUP ack=True fenced=False \| plan r5 wire[1,2,3] 1:TO_PICKUP@N1-3_N1-7:COMPLETED:BUSINESS,2:TO_PICKUP@C15-13:ARRIVED:BUSINESS,3:TO_DROPOFF@关卡:PLANNED:BUSINESS ack=True fenced=False \| plan r6 wire[1,2,3] 1:TO_PICKUP@N1-3_N1-7:COMPLETED:BUSINESS,2:TO_PICKUP@C15-13:COMPLETED:BUSINESS,3:TO_DROPOFF@关卡:ACTIVE:BUSINESS ack=True fenced=False \| worklist r3 @关卡 G3-08-A-20260921T184050411Z/DROPOFF,G3-08-B-20260921T184050411Z/DROPOFF ack=True fenced=False \| plan r7 wire[1,2,3] 1:TO_PICKUP@N1-3_N1-7:COMPLETED:BUSINESS,2:TO_PICKUP@C15-13:COMPLETED:BUSINESS,3:TO_DROPOFF@关卡:ARRIVED:BUSINESS ack=True fenced=False \| worklist r4 @关卡 G3-08-B-20260921T184050411Z/DROPOFF ack=True fenced=False` |
| 旅程走完：两条需求各一次装、一次卸、都 Committed，需求 Succeeded，旅程 Completed；关卡上两笔卸货各属一条需求；装过的两个仓最后关门、空、锁上、开锁输出复位（NO_DUPLICATE_COMMIT） | PASS | `Completed / A:Succeeded/ops 2/load 1/unload 1 B:Succeeded/ops 2/load 1/unload 1 / 两笔卸货两条需求 / 仓 CLOSED/EMPTY/1/0` | `Completed / A:Succeeded/ops 2/load 1/unload 1 B:Succeeded/ops 2/load 1/unload 1 / 卸货需求 2 笔 / 1=CLOSED/EMPTY/1/0 2=CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
