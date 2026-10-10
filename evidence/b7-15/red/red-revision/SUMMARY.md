# L2 场景证据：g3-multi-stop-plan

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T183729606Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `fe6783d5f7d4d639a6f384d2b74b4a47df9510fb` |
| onboardHmiCommit | `deeba94c42c51561621634194d3c9f6582737486` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T183729606Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 追加之前：车载端确认了派车那一版计划之后，计划腿列表的行数与行序等于服务端那一版的 sequence（DISPLAY_FULL_JOURNEY_PLAN） | PASS | `两条腿，行序 1,2` | `服务端 plan r1 wire[1,2] 1:TO_PICKUP@N1-3_N1-7:ACTIVE:BUSINESS,2:TO_DROPOFF@关卡:PLANNED:BUSINESS / 界面 1[1\|BUSINESS\|ACTIVE],2[2\|BUSINESS\|PLANNED]` |
| 未到达：没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用） | FAIL | `(reached)` | `(not reached) 没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用）` |
| 未到达：没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用） | FAIL | `(reached)` | `(not reached) 没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用）` |
| 未到达：没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用） | FAIL | `(reached)` | `(not reached) 没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用）` |
| 未到达：没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用） | FAIL | `(reached)` | `(not reached) 没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用）` |
| 未到达：没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用） | FAIL | `(reached)` | `(not reached) 没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用）` |
| 未到达：没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用） | FAIL | `(reached)` | `(not reached) 没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
