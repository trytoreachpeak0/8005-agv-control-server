# L2 场景证据：load-cancelled-before-sublot

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162842844Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162842844Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车到取货站停在等录入这一步，录入请求已送到车上、还没人回答 | PASS | `AwaitingSublot / 车收到 >= 1 次 / 未结算` | `AwaitingSublot / 车收到 1 次 / AcknowledgedAt=` |
| 授权 AUTHORIZED、slots 为空；车随后报 ALL_EMPTY 空结果并被服务端确认 | PASS | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 已确认` | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 确认于 10/10/2026 00:29:08` |
| 服务端的取消工作流：无 attempt、仓集合为空，收下的正是车报的那条结果，已收敛 | PASS | `LOAD_CANCELLATION / 无 attempt / [] / 9261a0c0-7bad-4900-be9a-faa1a335ae61 / Reconciled` | `LOAD_CANCELLATION / attempt= / [] / 9261a0c0-7bad-4900-be9a-faa1a335ae61 / Reconciled` |
| 旅程 Completed / CANCELLED_BY_OPERATOR，需求 Cancelled | PASS | `Completed / CANCELLED_BY_OPERATOR / Cancelled` | `Completed / CANCELLED_BY_OPERATOR / Cancelled` |
| 车辆占用释放了：用途占有记录有释放时刻，占有行已不在 | PASS | `占有记录已释放 / 占有行 0` | `ReleasedAt=2026-10-09 16:29:08.5515281+00:00 / 占有行 0` |
| 那条没人回答的录入请求被结算了（不会再重放进后面的会话） | PASS | `已结算` | `AcknowledgedAt=2026-10-09 16:29:08.5515281+00:00` |
| 全程没有仓位操作，也没有装货命令 | PASS | `0 / 0` | `0 / 0` |
| 取消收尾之后，车接了下一单 | PASS | `0 → AwaitingPickupArrival` | `0 → AwaitingPickupArrival` |
| 到站前重连走完完整握手、会话代前进，旅程仍在去取货站的路上 | PASS | `READY / > 1 / AwaitingPickupArrival` | `READY / 2 / AwaitingPickupArrival` |
| 录入请求是在重连之后、新连接上送到车的 | PASS | `>= 1 次` | `1 次` |
| 授权 AUTHORIZED、slots 为空；车随后报 ALL_EMPTY 空结果并被服务端确认 | PASS | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 已确认` | `0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 确认于 10/10/2026 00:29:13` |
| 服务端的取消工作流：无 attempt、仓集合为空，收下的正是车报的那条结果，已收敛 | PASS | `LOAD_CANCELLATION / 无 attempt / [] / 9f323a2a-d197-428c-a201-43acb3978760 / Reconciled` | `LOAD_CANCELLATION / attempt= / [] / 9f323a2a-d197-428c-a201-43acb3978760 / Reconciled` |
| 旅程 Completed / CANCELLED_BY_OPERATOR，需求 Cancelled | PASS | `Completed / CANCELLED_BY_OPERATOR / Cancelled` | `Completed / CANCELLED_BY_OPERATOR / Cancelled` |
| 车辆占用释放了：用途占有记录有释放时刻，占有行已不在 | PASS | `占有记录已释放 / 占有行 0` | `ReleasedAt=2026-10-09 16:29:13.2199563+00:00 / 占有行 0` |
| 那条没人回答的录入请求被结算了（不会再重放进后面的会话） | PASS | `已结算` | `AcknowledgedAt=2026-10-09 16:29:13.2199563+00:00` |
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
