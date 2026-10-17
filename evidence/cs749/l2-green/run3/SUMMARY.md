# L2 场景证据：remaining-plan-after-passed-stop

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T141705453Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `caca67f302fe83ffee2473f646576245054487f1` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `13eca82ac199bd97c1ad49d036c70cb5b012250f` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `128daae52758749d4d6f346231fdb336ca5d1079773bcab5430c20c53ce6b10b` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T141705453Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 乙途中追加进甲那一趟：停靠是 12 取、11 取、关卡卸（乙的卸货并进既有的关卡停靠） | PASS | `PICKUP@12 PICKUP@11 UNLOAD@210` | `PICKUP@12 PICKUP@11 UNLOAD@210` |
| 车离开 12 号站之后车载端确认的计划只有剩余停靠：11 取（ACTIVE，序号 1）、关卡卸（PLANNED，序号 2），没有 12 号站那条、没有 COMPLETED（DISPLAY_REMAINING_JOURNEY_PLAN） | PASS | `acked [1:C15-13:ACTIVE 2:关卡:PLANNED]` | `r4 acked [1:C15-13:ACTIVE 2:关卡:PLANNED]` |
| 库里 12 号站那个停靠已 COMPLETED、序位仍是 1：走过的站只是不下发，整趟的序位不变 | PASS | `COMPLETED / sequence 1` | `COMPLETED / sequence 1` |
| 走过一站之后追加的丙被受理进同一趟：归属三条，积压行有受理时刻，没有被任何 EN_ROUTE_APPEND_* 挡住 | PASS | `3 memberships / accepted` | `3 memberships / ACCEPTED accepted=yes` |
| 追加丙之后重发、车载端确认的计划只数剩余：11 取（当前，序号 1）、12 取（丙，新停靠）、关卡卸——三条，走过的 12 号站那个已完成停靠不在里面 | PASS | `acked [1:C15-13:ACTIVE 2:N1-3_N1-7:PLANNED 3:关卡:PLANNED]` | `r5 acked [1:C15-13:ACTIVE 2:N1-3_N1-7:PLANNED 3:关卡:PLANNED]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
