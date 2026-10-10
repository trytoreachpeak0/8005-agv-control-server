# L2 场景证据：waiting-journey-battery-watch

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160508050Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160508050Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 等满门槛后服务端日志里有这趟旅程的监看记录：ERR 级，阶段 AwaitingUnloadResult，电量 12%，写明低于救命线要人工挪车 | PASS | `[hh:mm:ss ERR] ... AwaitingUnloadResult ... battery 12% ... below the rescue line of 15% ...` | `[00:05:47 ERR] Journey journey:851eddf9-9cfe-471e-bbcd-9a5284df023e on vehicle AGV-L2-001 has waited for a person in AwaitingUnloadResult for 0 min; battery 12% as read at 2026-10-09T16:05:47.5212382+00:00. The battery is below the rescue line of 15%: a person has to move the vehicle to a charger. Nothing on this server will move it (REQ-0169).` |
| 端点列出这趟旅程：车、阶段、电量 12%、等级 BelowRescueLine、已过门槛，电量与库里监看记下的一致 | PASS | `AGV-L2-001 / AwaitingUnloadResult / 12 / BelowRescueLine / past threshold` | `AGV-L2-001 / AwaitingUnloadResult / 12 / BelowRescueLine / past=True / waited 5 s / db 12` |
| 看板「等人中的旅程」这一行写着闸口等卸货、12%、需要人工挪车充电 | PASS | `闸口等卸货 ... 12% ... 需要人工挪车充电` | `AGV-L2-001  闸口等卸货    0 分 5 秒  12%  2026-10-10 00:05:47  需要人工挪车充电：在车上用单机方式挪车、充电，不要在 RIoT 里给这辆车下单` |
| 报了但车没动：RIoT 上一张单都没多建，旅程仍停在 AwaitingUnloadResult | PASS | `2 orders / AwaitingUnloadResult` | `2 orders / AwaitingUnloadResult` |
| 卸货应答回来后旅程照常完成，端点不再列出它 | PASS | `Completed / (not listed)` | `Completed / (not listed)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
