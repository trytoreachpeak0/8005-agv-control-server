# L2 场景证据：station-deadline-sublot-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T151636745Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-5` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35361077376-1\_stage\l2-20260918T151636745Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站就开始计本站期限（不是等装货提交才开始） | PASS | `AwaitingSublot / 起点有值` | `AwaitingSublot / 09/18/2026 15:16:51 +00:00` |
| 期限未到：旅程原地等，录入请求还挂着没人回答 | PASS | `读于期限前 / AwaitingSublot / 请求未结算` | `读于期限前 17 s / AwaitingSublot / AcknowledgedAt=` |
| 到期结束本站：Completed / CANCELLED_BY_STATION_TIMEOUT，且不早于到站起算的期限 | PASS | `Completed / CANCELLED_BY_STATION_TIMEOUT / >= 2026-09-18T15:17:11.8424672+00:00` | `Completed / CANCELLED_BY_STATION_TIMEOUT / 2026-09-18T15:17:12.7975696+00:00` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |
| 调度租约与车辆占用都释放了 | PASS | `租约已释放 / 占用已释放` | `ReleasedAt=2026-09-18 15:17:12.7975696+00:00 / VehicleOccupancyReleasedAt=2026-09-18 15:17:12.7975696+00:00` |
| 那条没人回答的录入请求被结算了（不会再重放进后面的会话） | PASS | `已结算` | `AcknowledgedAt=2026-09-18 15:17:12.7975696+00:00` |
| 超时不下发任何仓位操作，也不开恢复流程 | PASS | `0 / 0` | `0 / 0` |
| 目录里仍有这一单，但它没有被再派：只有一条旅程、一条 RIoT 单 | PASS | `目录仍列出 / 1 条旅程 / 1 条 RIoT 单` | `目录列出 1 / 1 条旅程 / 1 条 RIoT 单` |
| 本站结束之后，车接了下一单 | PASS | `0 → AwaitingPickupArrival` | `0 → AwaitingPickupArrival` |
| 重连走完完整握手，会话代前进 | PASS | `READY / > 1` | `READY / 2` |
| 期限起点换成了重连之后的时刻（不是断联前那个起点接着走） | PASS | `>= 2026-09-18T15:17:28.9391350+00:00（原起点 2026-09-18T15:17:18.8412536+00:00）` | `2026-09-18T15:17:29.8088153+00:00（基线 2026-09-18T15:17:18.8412536+00:00）` |
| 原期限过去 3 秒，本站仍在等录入（断联那一轮的截止已作废） | PASS | `AwaitingSublot（读于 2026-09-18T15:17:41.8412536+00:00 之后、新期限之前）` | `AwaitingSublot（读于 2026-09-18T15:17:41.9811843+00:00）` |
| 按重连后重新计满的期限结束：不早于新起点加整段时长 | PASS | `CANCELLED_BY_STATION_TIMEOUT / >= 2026-09-18T15:17:49.8088153+00:00` | `CANCELLED_BY_STATION_TIMEOUT / 2026-09-18T15:17:50.7981474+00:00` |
| 全程两条 RIoT 单（两单各一条取货），没有关卡单 | PASS | `2 / 0` | `2 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
