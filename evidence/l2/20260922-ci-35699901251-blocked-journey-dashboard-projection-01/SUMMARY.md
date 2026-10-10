# L2 场景证据：blocked-journey-dashboard-projection

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T074623393Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-5` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T074623393Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 行驶中没有阻断时端点不列这条旅程 | PASS | `(not listed)` | `(not listed)` |
| 检查点等待挂上后端点列出车、站、阻断码与开始时间，开始时间就是库里记下的那一个 | PASS | `AGV-L2-001 / N1-3_N1-7 / VEHICLE_WAITING_AT_CHECKPOINT since 2026-09-22 07:46:41.8984527+00:00 (Operator)` | `AGV-L2-001 / N1-3_N1-7 / VEHICLE_WAITING_AT_CHECKPOINT since 2026-09-22T07:46:41.8984527+00:00 (0 s, Operator, AwaitingPickupArrival @ N1-3_N1-7)` |
| 码被清掉后端点不再列出这条旅程，库里阻断码与开始时间一并清空 | PASS | `(not listed) / code null / since null` | `(not listed) / code '' / since ''` |
| 会话未就绪挂上后端点带开始时间与会话的原因码、安全原因码、SafetyUnknownPresent，安全证据不全直接最高档 | PASS | `ONBOARD_SESSION_NOT_READY with since, session reason, ["IO_FACT_UNKNOWN"], unknown=True, MaintenanceAdministrator` | `ONBOARD_SESSION_NOT_READY since 2026-09-22T07:46:43.8558789+00:00 (0 s, MaintenanceAdministrator, AwaitingPickupArrival @ N1-3_N1-7); session {"present":true,"reasonCode":"DEPARTURE_SAFETY_NOT_READY","safetyReasonCodesJson":"[\"IO_FACT_UNKNOWN\"]","safetyUnknownPresent":true}` |
| 这一行被后续写入（同码、UpdatedAt 前移）之后，端点与库里的开始时间都不变，已挂时长照实增长 | PASS | `since 2026-09-22T07:46:43.8558789+00:00, UpdatedAt > since + 2 s, blockedSeconds > 0` | `since 2026-09-22T07:46:43.8558789+00:00 (db 2026-09-22 07:46:43.8558789+00:00), UpdatedAt 2026-09-22 07:46:46.8491674+00:00, 3 s` |
| 看板的旅程阻断卡片渲染出这条阻断：车、取货站、阻断码、安全原因码，按维护管理员那一档上色 | PASS | `escalation-maintenance-administrator AGV-L2-001 N1-3_N1-7 ONBOARD_SESSION_NOT_READY ... IO_FACT_UNKNOWN` | `escalation-maintenance-administrator AGV-L2-001  N1-3_N1-7  ONBOARD_SESSION_NOT_READY  2026-09-22 15:46:43  0 分 3 秒  维护管理员  原因码 DEPARTURE_SAFETY_NOT_READY；安全原因码 ["IO_FACT_UNKNOWN"]；安全证据有未知项：是  L2-SUBLOT-20260922T074623393Z\|WIRE_TO_GATE：待装` |
| 到站换段之后会话未就绪那条阻断已清掉，端点不再列出这条旅程 | PASS | `(not listed) / code null` | `(not listed) / code ''` |
| 仓门未闭告警挂上后端点列出 AwaitingLoadResult（不是 Blocked）、取货站与开始时间，开始时间就是库里记下的那一个，按操作员档 | PASS | `(not listed) → AwaitingLoadResult @ N1-3_N1-7, STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-22 07:47:18.8463069+00:00 (Operator)` | `(not listed) → STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-22T07:47:18.8463069+00:00 (0 s, Operator, AwaitingLoadResult @ N1-3_N1-7)` |
| 告警挂着期间运行时又跑了几轮，码与开始时间不变，stage 仍是 AwaitingLoadResult | PASS | `STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-22T07:47:18.8463069+00:00 (AwaitingLoadResult)` | `STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-22T07:47:18.8463069+00:00 (4 s, Operator, AwaitingLoadResult @ N1-3_N1-7)` |
| 门关上后端点不再列出这条旅程，库里阻断码与开始时间一并清空，本站仍在等装货结果 | PASS | `(not listed) / code null / since null / AwaitingLoadResult` | `(not listed) / code '' / since '' / AwaitingLoadResult` |
| 装货结果需要恢复挂上后端点列出 Blocked、取货站与开始时间，开始时间就是库里记下的那一个 | PASS | `Blocked @ N1-3_N1-7, LOAD_RESULT_REQUIRES_RECOVERY since 2026-09-22 07:47:24.8543534+00:00` | `LOAD_RESULT_REQUIRES_RECOVERY since 2026-09-22T07:47:24.8543534+00:00 (0 s, Operator, Blocked @ N1-3_N1-7)` |
| 停摆期间运行时又跑了几轮，阻断码与开始时间不变 | PASS | `LOAD_RESULT_REQUIRES_RECOVERY since 2026-09-22T07:47:24.8543534+00:00` | `LOAD_RESULT_REQUIRES_RECOVERY since 2026-09-22T07:47:24.8543534+00:00 (4 s, Operator, Blocked @ N1-3_N1-7)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
