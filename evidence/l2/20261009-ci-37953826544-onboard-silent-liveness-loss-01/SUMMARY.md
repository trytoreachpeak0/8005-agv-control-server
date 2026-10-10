# L2 场景证据：onboard-silent-liveness-loss

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160403291Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160403291Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车在路上、会话正常时端点不列这条旅程 | PASS | `(not listed)` | `(not listed)` |
| 车载端停发但不断线，服务端按 ADR-cross-0027 自己关掉这条连接（对端读到 EOF） | PASS | `READY -> DISCONNECTED` | `READY -> DISCONNECTED` |
| 静默期间车载端本该发出的报文被丢掉，不是「它本来就没话说」 | PASS | `至少一条 dropped` | `3 条 dropped` |
| 失联期间在途旅程挂上 ONBOARD_SESSION_LOST，端点列出这条旅程、带开始时间，开始时间就是库里记下的那一个 | PASS | `ONBOARD_SESSION_LOST since 2026-10-09 16:04:33.5908392+00:00` | `ONBOARD_SESSION_LOST since 2026-10-09T16:04:33.5908392+00:00 (0 s, MaintenanceAdministrator, AwaitingPickupArrival)` |
| 失联直接进最高档：会话行上的安全判定是车最后一次在线时的，说不了现在（REQ-0269） | PASS | `MaintenanceAdministrator` | `MaintenanceAdministrator` |
| 挂着期间又跑了几轮，阻断码与开始时间不变，阶段仍是 AwaitingPickupArrival | PASS | `ONBOARD_SESSION_LOST since 2026-10-09T16:04:33.5908392+00:00（AwaitingPickupArrival）` | `ONBOARD_SESSION_LOST since 2026-10-09T16:04:33.5908392+00:00 (4 s, MaintenanceAdministrator, AwaitingPickupArrival)（AwaitingPickupArrival）` |
| 失联不结束需求、不释放车辆占用（用途占有记录仍开着） | PASS | `Accepted / 0 条已释放的用途占有记录 / 这条需求恰有 1 条开着的` | `Accepted / 0 条已释放的用途占有记录 / 这条需求 1 条开着的` |
| 失联期间订单命令面一次都没被调过：本批不发 OrderHold（用户 2026-09-20 定） | PASS | `命令审计表仍是 0 行` | `0 行` |
| 车重连之后失联码被清掉（或落进「已重连、握手未完」那个窗口，换成会话未就绪），两种之外都算红 | PASS | `ONBOARD_SESSION_LOST -> (not listed) 或 ONBOARD_SESSION_NOT_READY` | `ONBOARD_SESSION_LOST -> (not listed)` |
| 到站换段之后阻断码清空，端点不再列出这条旅程，旅程照常往下走 | PASS | `AwaitingSublot / (not listed) / code null / since null` | `AwaitingSublot / (not listed) / code '' / since ''` |
| 全程订单命令面一次都没被调过 | PASS | `命令审计表仍是 0 行` | `0 行` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
