# L2 场景证据：load-determinate-failure-and-door-open-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160107157Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-5` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160107157Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 期限未到、结果未到：旅程原地等装货结果，仓位操作仍在途 | PASS | `读于期限前 / AwaitingLoadResult / Prepared` | `读于期限前 17.5 s / AwaitingLoadResult / Prepared` |
| 确定失败是在本站期限之后收到的（这一格的前提） | PASS | `>= 2026-10-09T16:01:52.2707519+00:00` | `2026-10-09T16:01:53.3863778+00:00` |
| 仓位操作判 Failed（不是 RecoveryRequired），需求 Cancelled、理由 CANCELLED_BY_STATION_TIMEOUT，旅程 Completed | PASS | `Failed / Cancelled / Completed / CANCELLED_BY_STATION_TIMEOUT` | `Failed / Cancelled / Completed / CANCELLED_BY_STATION_TIMEOUT` |
| 车辆占用释放（占有记录有释放时刻、占有行已不在），悬空的装货命令被结算（不会再重放进后面的会话） | PASS | `占有记录已释放 / 占有行 0 / 装货命令已结算` | `ReleasedAt=2026-10-09 16:01:53.6394209+00:00 / 占有行 0 / AcknowledgedAt=2026-10-09 16:01:53.6394209+00:00` |
| 没有恢复：没有恢复工作流、没有异常恢复会话，这台车的会话仍是 Ready | PASS | `0 / 0 / Ready` | `0 / 0 / Ready (READY)` |
| 确定失败结算之后，同一台车接了下一单 | PASS | `0 → AwaitingPickupArrival on AGV-L2-001` | `0 → AwaitingPickupArrival on AGV-L2-001` |
| 门开着时会话仍是 Ready：开着的门由本车自己在途的装货解释 | PASS | `Ready` | `Ready (READY)` |
| 期限未到：门开着也不挂告警 | PASS | `读于期限前 / AwaitingLoadResult / 无阻断码` | `读于期限前 17 s / AwaitingLoadResult / ''` |
| 告警挂上：stage 仍是 AwaitingLoadResult（不是 Blocked）、开始时间不早于期限、需求与用途占有记录都没动 | PASS | `AwaitingLoadResult / since >= 2026-10-09T16:02:15.7176895+00:00 / Prepared / Accepted / 占有记录未释放` | `AwaitingLoadResult / since 2026-10-09 16:02:16.6447156+00:00 / Prepared / Accepted / ReleasedAt=''` |
| 告警挂着期间运行时又跑了几轮：仍在 AwaitingLoadResult、同一个码、开始时间不变 | PASS | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-10-09 16:02:16.6447156+00:00` | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-10-09 16:02:16.6447156+00:00` |
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
