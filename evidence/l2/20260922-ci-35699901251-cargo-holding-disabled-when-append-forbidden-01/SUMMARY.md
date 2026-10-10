# L2 场景证据：cargo-holding-disabled-when-append-forbidden

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T073846542Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T073846542Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：当前参数版本里 MAP-25-WIRE_TO_GATE 的途中追加上限是 0 | PASS | `0` | `0` |
| 上限为 0：装货阶段从没进入持货等单或整车满，离站前已是 CLOSED/PLANNED_LOADING_COMPLETE | PASS | `no WAIT/FULL; CLOSED/PLANNED_LOADING_COMPLETE` | `seen CLOSED/PLANNED_LOADING_COMPLETE,LOADING(null); now AwaitingGateArrival CLOSED/PLANNED_LOADING_COMPLETE` |
| 上限为 0：装完到关卡腿建单不超过 25 秒（站点等待 10 秒；持货的话至少 40 秒） | PASS | `<= 25 s` | `11.1 s` |
| 上限为 0：这一趟发给车的快照没有一张带持货期限，状态只有 LOADING 与 CLOSED/PLANNED_LOADING_COMPLETE；旅程走完 | PASS | `LOADING, CLOSED/PLANNED_LOADING_COMPLETE; no deadline; Completed` | `CLOSED/PLANNED_LOADING_COMPLETE, LOADING; 0 with deadline; Completed CLOSED/PLANNED_LOADING_COMPLETE` |
| 服务端不停导入一版新参数：MAP-25-WIRE_TO_GATE 的途中追加上限为空（未配置） | PASS | `OK, (unconfigured)` | `OK, ''` |
| 上限未配置：装货阶段从没进入持货等单或整车满，离站前已是 CLOSED/PLANNED_LOADING_COMPLETE | PASS | `no WAIT/FULL; CLOSED/PLANNED_LOADING_COMPLETE` | `seen CLOSED/PLANNED_LOADING_COMPLETE,LOADING(null); now AwaitingDepartureSafety CLOSED/PLANNED_LOADING_COMPLETE` |
| 上限未配置：装完到关卡腿建单不超过 25 秒（站点等待 10 秒；持货的话至少 40 秒） | PASS | `<= 25 s` | `11.1 s` |
| 上限未配置：这一趟发给车的快照没有一张带持货期限，状态只有 LOADING 与 CLOSED/PLANNED_LOADING_COMPLETE；旅程走完 | PASS | `LOADING, CLOSED/PLANNED_LOADING_COMPLETE; no deadline; Completed` | `CLOSED/PLANNED_LOADING_COMPLETE, LOADING; 0 with deadline; Completed CLOSED/PLANNED_LOADING_COMPLETE` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
