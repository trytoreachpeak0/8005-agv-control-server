# L2 场景证据：waiting-journey-battery-watch

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T102235173Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `7fcdc6cf37c373015fe9ca05a566d59cd5bfe0f5` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260922T102235173Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 等满门槛后服务端日志里有这趟旅程的监看记录：ERR 级，阶段 AwaitingUnloadResult，电量 12%，写明低于救命线要人工挪车 | PASS | `[hh:mm:ss ERR] ... AwaitingUnloadResult ... battery 12% ... below the rescue line of 15% ...` | `[18:23:29 ERR] Journey journey:0be81baf-0791-4962-b6aa-f1a9e0dc1352 on vehicle AGV-L2-001 has waited for a person in AwaitingUnloadResult for 0 min; battery 12% as read at 2026-09-22T10:23:29.1934510+00:00. The battery is below the rescue line of 15%: a person has to move the vehicle to a charger. Nothing on this server will move it (REQ-0169).` |
| 端点列出这趟旅程：车、阶段、电量 12%、等级 BelowRescueLine、已过门槛，电量与库里监看记下的一致 | PASS | `AGV-L2-001 / AwaitingUnloadResult / 12 / BelowRescueLine / past threshold` | `AGV-L2-001 / AwaitingUnloadResult / 12 / BelowRescueLine / past=True / waited 5 s / db 12` |
| 看板「等人中的旅程」这一行写着闸口等卸货、12%、需要人工挪车充电 | PASS | `闸口等卸货 ... 12% ... 需要人工挪车充电` | `AGV-L2-001  闸口等卸货    0 分 5 秒  12%  2026-09-22 18:23:29  需要人工挪车充电` |
| 报了但车没动：RIoT 上一张单都没多建，旅程仍停在 AwaitingUnloadResult | PASS | `2 orders / AwaitingUnloadResult` | `2 orders / AwaitingUnloadResult` |
| 卸货应答回来后旅程照常完成，端点不再列出它 | PASS | `Completed / (not listed)` | `Completed / (not listed)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
