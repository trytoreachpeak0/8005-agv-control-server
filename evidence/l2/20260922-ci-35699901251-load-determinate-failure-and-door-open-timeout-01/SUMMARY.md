# L2 场景证据：load-determinate-failure-and-door-open-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T073518038Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T073518038Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 期限未到、结果未到：旅程原地等装货结果，仓位操作仍在途 | PASS | `读于期限前 / AwaitingLoadResult / Prepared` | `读于期限前 17.3 s / AwaitingLoadResult / Prepared` |
| 确定失败是在本站期限之后收到的（这一格的前提） | PASS | `>= 2026-09-22T07:35:54.8834399+00:00` | `2026-09-22T07:35:55.9360126+00:00` |
| 仓位操作判 Failed（不是 RecoveryRequired），需求 Cancelled、理由 CANCELLED_BY_STATION_TIMEOUT，旅程 Completed | PASS | `Failed / Cancelled / Completed / CANCELLED_BY_STATION_TIMEOUT` | `Failed / Cancelled / Completed / CANCELLED_BY_STATION_TIMEOUT` |
| 租约与车辆占用释放，悬空的装货命令被结算（不会再重放进后面的会话） | PASS | `租约已释放 / 占用已释放 / 装货命令已结算` | `ReleasedAt=2026-09-22 07:35:56.4096182+00:00 / VehicleOccupancyReleasedAt=2026-09-22 07:35:56.4096182+00:00 / AcknowledgedAt=2026-09-22 07:35:56.4096182+00:00` |
| 没有恢复：没有恢复工作流、没有异常恢复会话，这台车的会话仍是 Ready | PASS | `0 / 0 / Ready` | `0 / 0 / Ready (READY)` |
| 确定失败结算之后，同一台车接了下一单 | PASS | `0 → AwaitingPickupArrival on AGV-L2-001` | `0 → AwaitingPickupArrival on AGV-L2-001` |
| 门开着时会话仍是 Ready：开着的门由本车自己在途的装货解释 | PASS | `Ready` | `Ready (READY)` |
| 期限未到：门开着也不挂告警 | PASS | `读于期限前 / AwaitingLoadResult / 无阻断码` | `读于期限前 16.9 s / AwaitingLoadResult / ''` |
| 告警挂上：stage 仍是 AwaitingLoadResult（不是 Blocked）、开始时间不早于期限、需求与租约都没动 | PASS | `AwaitingLoadResult / since >= 2026-09-22T07:36:18.4832967+00:00 / Prepared / Accepted / 租约未释放` | `AwaitingLoadResult / since 2026-09-22 07:36:19.4259841+00:00 / Prepared / Accepted / ReleasedAt=''` |
| 告警挂着期间运行时又跑了几轮：仍在 AwaitingLoadResult、同一个码、开始时间不变 | PASS | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-22 07:36:19.4259841+00:00` | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-22 07:36:19.4259841+00:00` |
| 门关上后告警撤销、开始时间一并清掉，本站仍在等结果（关门本身不结束本站） | PASS | `(cleared) / since null / AwaitingLoadResult` | `(cleared) / since '' / AwaitingLoadResult` |
| 报 COMPLETED 后仓位操作 Committed、旅程离开 AwaitingLoadResult，告警码不在 | PASS | `Committed / AwaitingStationDeparture 或其后 / 非 STATION_TIMEOUT_DOOR_NOT_CLOSED` | `Committed / AwaitingStationDeparture / ''` |
| 全程没有进过恢复：没有恢复工作流、没有异常恢复会话 | PASS | `0 / 0` | `0 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
