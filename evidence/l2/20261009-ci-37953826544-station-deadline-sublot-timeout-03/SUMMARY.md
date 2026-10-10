# L2 场景证据：station-deadline-sublot-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T155827030Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T155827030Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站就开始计本站期限（不是等装货提交才开始） | PASS | `AwaitingSublot / 起点有值` | `AwaitingSublot / 10/09/2026 15:58:51 +00:00` |
| 期限未到：旅程原地等，录入请求还挂着没人回答 | PASS | `读于期限前 / AwaitingSublot / 请求未结算` | `读于期限前 17.3 s / AwaitingSublot / AcknowledgedAt=` |
| 到期结束本站：Completed / CANCELLED_BY_STATION_TIMEOUT，且不早于到站起算的期限 | PASS | `Completed / CANCELLED_BY_STATION_TIMEOUT / >= 2026-10-09T15:59:11.0024684+00:00` | `Completed / CANCELLED_BY_STATION_TIMEOUT / 2026-10-09T15:59:11.4759408+00:00` |
| 需求终态为 Cancelled | PASS | `Cancelled` | `Cancelled` |
| 车辆占用释放了：用途占有记录有释放时刻，占有行已不在 | PASS | `占有记录已释放 / 占有行 0` | `ReleasedAt=2026-10-09 15:59:11.4759408+00:00 / 占有行 0` |
| 那条没人回答的录入请求被结算了（不会再重放进后面的会话） | PASS | `已结算` | `AcknowledgedAt=2026-10-09 15:59:11.4759408+00:00` |
| 超时不下发任何仓位操作，也不开恢复流程 | PASS | `0 / 0` | `0 / 0` |
| 本站结束之后，车收到并确认了一张空清单：同一个站、号比到站那一版大（control-server#323） | PASS | `空清单 @N1-3_N1-7 / r > 1 / 已确认` | `r2@N1-3_N1-7 ack=True` |
| 目录里仍有这一单，但它没有被再派：只有一条旅程、一条 RIoT 单 | PASS | `目录仍列出 / 1 条旅程 / 1 条 RIoT 单` | `目录列出 1 / 1 条旅程 / 1 条 RIoT 单` |
| 本站结束之后，车接了下一单 | PASS | `0 → AwaitingPickupArrival` | `0 → AwaitingPickupArrival` |
| 重连走完完整握手，会话代前进 | PASS | `READY / > 1` | `READY / 2` |
| 期限起点换成了重连之后的时刻（不是断联前那个起点接着走） | PASS | `>= 2026-10-09T15:59:27.6644580+00:00（原起点 2026-10-09T15:59:17.5325965+00:00）` | `2026-10-09T15:59:28.4321263+00:00（基线 2026-10-09T15:59:17.5325965+00:00）` |
| 原期限过去 3 秒，本站仍在等录入（断联那一轮的截止已作废） | PASS | `AwaitingSublot（读于 2026-10-09T15:59:40.5325965+00:00 之后、新期限之前）` | `AwaitingSublot（读于 2026-10-09T15:59:40.5448912+00:00）` |
| 重连之后，车收到并确认了一版号更大的清单，带的正是重填后的期限（control-server#339） | PASS | `r > 3 / 期限 2026-10-09T15:59:48.4321263+00:00 / 已确认` | `r3 2026-10-09T15:59:37.5325965+00:00 ack=True, r4 2026-10-09T15:59:48.4321263+00:00 ack=True` |
| 按重连后重新计满的期限结束：不早于新起点加整段时长 | PASS | `CANCELLED_BY_STATION_TIMEOUT / >= 2026-10-09T15:59:48.4321263+00:00` | `CANCELLED_BY_STATION_TIMEOUT / 2026-10-09T15:59:48.4617773+00:00` |
| 全程两条 RIoT 单（两单各一条取货），没有关卡单 | PASS | `2 / 0` | `2 / 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
