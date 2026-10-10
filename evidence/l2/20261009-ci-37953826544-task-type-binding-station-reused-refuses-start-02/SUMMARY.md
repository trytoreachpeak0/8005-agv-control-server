# L2 场景证据：task-type-binding-station-reused-refuses-start

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162737016Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
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
| protocolReleaseIdentity.source | `appsettings.json:ProtocolCandidate` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162737016Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端进程以非零退出码结束 | PASS | `非零` | `-532462766` |
| /health/live 始终不通 | PASS | `从未答过` | `从未答过` |
| 服务端日志含 TASK_TYPE_STATION_REUSED | PASS | `TASK_TYPE_STATION_REUSED` | `[00:27:46 ERR] Task type station preset C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162737016Z-slot4\task-type-stations.settings.json refused: TASK_TYPE_STATION_REUSED Station 关卡/210 is bound to 2 task types (STAGING_TO_WIRE, WIRE_TO_GATE); one station serves at most one task type.` |
| 拒绝点名 Station 关卡/210 与 WIRE_TO_GATE、STAGING_TO_WIRE | PASS | `关卡/210 + WIRE_TO_GATE + STAGING_TO_WIRE` | `[00:27:46 ERR] Task type station preset C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162737016Z-slot4\task-type-stations.settings.json refused: TASK_TYPE_STATION_REUSED Station 关卡/210 is bound to 2 task types (STAGING_TO_WIRE, WIRE_TO_GATE); one station serves at most one task type.` |
| 规则版本与绑定集版本都没有落库 | PASS | `Rules=0 BindingSets=0` | `Rules=0 BindingSets=0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
