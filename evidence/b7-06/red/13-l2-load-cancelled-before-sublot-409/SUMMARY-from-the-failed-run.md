# L2 场景证据：load-cancelled-before-sublot

结论：**FAIL**

失败原因：响应状态代码未指示成功: 409 (Conflict)。

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T233547340Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-5` |
| controlServerCommit | `dbd63230cf4d7551f55bf285116bf733a414c486` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35544281388-1\_stage\l2-20260920T233547340Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站停在等录入这一步，录入请求已送到车上、还没人回答 | PASS | `AwaitingSublot / 车收到 >= 1 次 / 未结算` | `AwaitingSublot / 车收到 1 次 / AcknowledgedAt=` |
| 授权 AUTHORIZED、slots 为空；车随后报 ALL_EMPTY 空结果并被服务端确认 | PASS | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 已确认` | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 确认于 09/21/2026 07:36:04` |
| 服务端的取消工作流：无 attempt、仓集合为空，收下的正是车报的那条结果，已收敛 | PASS | `LOAD_CANCELLATION / 无 attempt / [] / e8ae5e69-c044-4b83-ac38-5e3907f26edb / Reconciled` | `LOAD_CANCELLATION / attempt= / [] / e8ae5e69-c044-4b83-ac38-5e3907f26edb / Reconciled` |
| 旅程 Completed / CANCELLED_BY_OPERATOR，需求 Cancelled | PASS | `Completed / CANCELLED_BY_OPERATOR / Cancelled` | `Completed / CANCELLED_BY_OPERATOR / Cancelled` |
| 调度租约与车辆占用都释放了 | PASS | `租约已释放 / 占用已释放` | `ReleasedAt=2026-09-20 23:36:04.4945042+00:00 / VehicleOccupancyReleasedAt=2026-09-20 23:36:04.4945042+00:00` |
| 那条没人回答的录入请求被结算了（不会再重放进后面的会话） | PASS | `已结算` | `AcknowledgedAt=2026-09-20 23:36:04.4945042+00:00` |
| 全程没有仓位操作，也没有装货命令 | PASS | `0 / 0` | `0 / 0` |
| 取消收尾之后，车接了下一单 | PASS | `0 → AwaitingPickupArrival` | `0 → AwaitingPickupArrival` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
