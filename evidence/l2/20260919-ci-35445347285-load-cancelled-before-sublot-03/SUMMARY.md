# L2 场景证据：load-cancelled-before-sublot

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T133426855Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-5` |
| controlServerCommit | `50dc987d680a33c3f8732619a91f5761453ddd63` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35445347285-1\_stage\l2-20260919T133426855Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站停在等录入这一步，录入请求已送到车上、还没人回答 | PASS | `AwaitingSublot / 车收到 >= 1 次 / 未结算` | `AwaitingSublot / 车收到 1 次 / AcknowledgedAt=` |
| 授权 AUTHORIZED、slots 为空；车随后报 ALL_EMPTY 空结果并被服务端确认 | PASS | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 已确认` | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 确认于 09/19/2026 21:34:43` |
| 服务端的取消工作流：无 attempt、仓集合为空，收下的正是车报的那条结果，已收敛 | PASS | `LOAD_CANCELLATION / 无 attempt / [] / 7b4881e3-a8ab-4806-8c1c-708af8678755 / Reconciled` | `LOAD_CANCELLATION / attempt= / [] / 7b4881e3-a8ab-4806-8c1c-708af8678755 / Reconciled` |
| 旅程 Completed / CANCELLED_BY_OPERATOR，需求 Cancelled | PASS | `Completed / CANCELLED_BY_OPERATOR / Cancelled` | `Completed / CANCELLED_BY_OPERATOR / Cancelled` |
| 调度租约与车辆占用都释放了 | PASS | `租约已释放 / 占用已释放` | `ReleasedAt=2026-09-19 13:34:43.3107132+00:00 / VehicleOccupancyReleasedAt=2026-09-19 13:34:43.3107132+00:00` |
| 那条没人回答的录入请求被结算了（不会再重放进后面的会话） | PASS | `已结算` | `AcknowledgedAt=2026-09-19 13:34:43.3107132+00:00` |
| 全程没有仓位操作，也没有装货命令 | PASS | `0 / 0` | `0 / 0` |
| 取消收尾之后，车接了下一单 | PASS | `0 → AwaitingPickupArrival` | `0 → AwaitingPickupArrival` |
| 到站前重连走完完整握手、会话代前进，旅程仍在去取货站的路上 | PASS | `READY / > 1 / AwaitingPickupArrival` | `READY / 2 / AwaitingPickupArrival` |
| 录入请求是在重连之后、新连接上送到车的 | PASS | `>= 1 次` | `1 次` |
| 授权 AUTHORIZED、slots 为空；车随后报 ALL_EMPTY 空结果并被服务端确认 | PASS | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 已确认` | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 确认于 09/19/2026 21:34:47` |
| 服务端的取消工作流：无 attempt、仓集合为空，收下的正是车报的那条结果，已收敛 | PASS | `LOAD_CANCELLATION / 无 attempt / [] / 2bde4d08-59ea-4aaf-8e8d-b0bd4b53d176 / Reconciled` | `LOAD_CANCELLATION / attempt= / [] / 2bde4d08-59ea-4aaf-8e8d-b0bd4b53d176 / Reconciled` |
| 旅程 Completed / CANCELLED_BY_OPERATOR，需求 Cancelled | PASS | `Completed / CANCELLED_BY_OPERATOR / Cancelled` | `Completed / CANCELLED_BY_OPERATOR / Cancelled` |
| 调度租约与车辆占用都释放了 | PASS | `租约已释放 / 占用已释放` | `ReleasedAt=2026-09-19 13:34:47.4227215+00:00 / VehicleOccupancyReleasedAt=2026-09-19 13:34:47.4227215+00:00` |
| 那条没人回答的录入请求被结算了（不会再重放进后面的会话） | PASS | `已结算` | `AcknowledgedAt=2026-09-19 13:34:47.4227215+00:00` |
| 全程没有仓位操作，也没有装货命令 | PASS | `0 / 0` | `0 / 0` |
| 重连之后车没有再收到录入请求，库里也没有未结算的录入请求 | PASS | `READY / 0 / 0` | `READY / 0 / 0` |
| 全程两条 RIoT 单、没有关卡单，车没收到过仓位命令 | PASS | `2 / 0 / 0` | `2 / 0 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
