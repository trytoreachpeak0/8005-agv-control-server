# L2 场景证据：onboard-silent-liveness-loss

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T070208691Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `f9a4e372a0d49b6e73179a55fc48a7860a401bb7` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260920T070208691Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车在路上、会话正常时端点不列这条旅程 | PASS | `(not listed)` | `(not listed)` |
| 车载端停发但不断线，服务端按 ADR-cross-0027 自己关掉这条连接（对端读到 EOF） | PASS | `READY -> DISCONNECTED` | `READY -> DISCONNECTED` |
| 静默期间车载端本该发出的报文被丢掉，不是「它本来就没话说」 | PASS | `至少一条 dropped` | `9 条 dropped` |
| 失联期间在途旅程挂上 ONBOARD_SESSION_LOST，端点列出这条旅程、带开始时间，开始时间就是库里记下的那一个 | PASS | `ONBOARD_SESSION_LOST since 2026-09-20 07:02:33.0951811+00:00` | `ONBOARD_SESSION_LOST since 2026-09-20T07:02:33.0951811+00:00 (0 s, MaintenanceAdministrator, AwaitingPickupArrival)` |
| 失联直接进最高档：会话行上的安全判定是车最后一次在线时的，说不了现在（REQ-0269） | PASS | `MaintenanceAdministrator` | `MaintenanceAdministrator` |
| 挂着期间又跑了几轮，阻断码与开始时间不变，阶段仍是 AwaitingPickupArrival | PASS | `ONBOARD_SESSION_LOST since 2026-09-20T07:02:33.0951811+00:00（AwaitingPickupArrival）` | `ONBOARD_SESSION_LOST since 2026-09-20T07:02:33.0951811+00:00 (4 s, MaintenanceAdministrator, AwaitingPickupArrival)（AwaitingPickupArrival）` |
| 失联不结束需求、不释放租约 | PASS | `Accepted / 0 条已释放的租约` | `Accepted / 0 条已释放的租约` |
| 失联期间订单命令面一次都没被调过：本批不发 OrderHold（用户 2026-09-20 定） | PASS | `命令审计表仍是 0 行` | `0 行` |
| 车重连之后这条旅程不再挂失联码（重连开新代次，先走既有的会话未就绪那条路） | PASS | `ONBOARD_SESSION_LOST -> 别的` | `ONBOARD_SESSION_LOST -> (not listed)` |
| 到站换段之后阻断码清空，端点不再列出这条旅程，旅程照常往下走 | PASS | `AwaitingSublot / (not listed) / code null / since null` | `AwaitingSublot / (not listed) / code '' / since ''` |
| 全程订单命令面一次都没被调过 | PASS | `命令审计表仍是 0 行` | `0 行` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
