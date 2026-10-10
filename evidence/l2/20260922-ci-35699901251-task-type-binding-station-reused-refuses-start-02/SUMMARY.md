# L2 场景证据：task-type-binding-station-reused-refuses-start

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T075844555Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
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
| protocolReleaseIdentity.source | `appsettings.json:ProtocolCandidate` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T075844555Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端进程以非零退出码结束 | PASS | `非零` | `-532462766` |
| /health/live 始终不通 | PASS | `从未答过` | `从未答过` |
| 服务端日志含 TASK_TYPE_STATION_REUSED | PASS | `TASK_TYPE_STATION_REUSED` | `[15:58:51 ERR] Task type station preset C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T075844555Z-slot4\task-type-stations.settings.json refused: TASK_TYPE_STATION_REUSED Station 关卡/210 is bound to 2 task types (STAGING_TO_WIRE, WIRE_TO_GATE); one station serves at most one task type.` |
| 拒绝点名 Station 关卡/210 与 WIRE_TO_GATE、STAGING_TO_WIRE | PASS | `关卡/210 + WIRE_TO_GATE + STAGING_TO_WIRE` | `[15:58:51 ERR] Task type station preset C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T075844555Z-slot4\task-type-stations.settings.json refused: TASK_TYPE_STATION_REUSED Station 关卡/210 is bound to 2 task types (STAGING_TO_WIRE, WIRE_TO_GATE); one station serves at most one task type.` |
| 规则版本与绑定集版本都没有落库 | PASS | `Rules=0 BindingSets=0` | `Rules=0 BindingSets=0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
