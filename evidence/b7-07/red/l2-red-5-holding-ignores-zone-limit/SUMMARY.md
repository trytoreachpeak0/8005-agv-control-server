# L2 场景证据：cargo-holding-disabled-when-append-forbidden

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260921T060956160Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `ac24df1c6ad6f51a1a6bea43784a4562d1d4661c` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260921T060956160Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：当前参数版本里 MAP-25-WIRE_TO_GATE 的途中追加上限是 0 | PASS | `0` | `0` |
| 上限为 0：装货阶段从没进入持货等单或整车满，离站前已是 CLOSED/PLANNED_LOADING_COMPLETE | FAIL | `no WAIT/FULL; CLOSED/PLANNED_LOADING_COMPLETE` | `seen CARGO_HOLDING_WAIT,CLOSED/CARGO_HOLDING_TIMEOUT,LOADING(null); now AwaitingGateArrival CLOSED/CARGO_HOLDING_TIMEOUT` |
| 上限为 0：装完到关卡腿建单不超过 25 秒（站点等待 10 秒；持货的话至少 40 秒） | FAIL | `<= 25 s` | `40.1 s` |
| 上限为 0：这一趟发给车的快照没有一张带持货期限，状态只有 LOADING 与 CLOSED/PLANNED_LOADING_COMPLETE；旅程走完 | FAIL | `LOADING, CLOSED/PLANNED_LOADING_COMPLETE; no deadline; Completed` | `CARGO_HOLDING_WAIT, CLOSED/CARGO_HOLDING_TIMEOUT, LOADING; 1 with deadline; Completed CLOSED/CARGO_HOLDING_TIMEOUT` |
| 服务端不停导入一版新参数：MAP-25-WIRE_TO_GATE 的途中追加上限为空（未配置） | PASS | `OK, (unconfigured)` | `OK, ''` |
| 上限未配置：装货阶段从没进入持货等单或整车满，离站前已是 CLOSED/PLANNED_LOADING_COMPLETE | FAIL | `no WAIT/FULL; CLOSED/PLANNED_LOADING_COMPLETE` | `seen CARGO_HOLDING_WAIT,CLOSED/CARGO_HOLDING_TIMEOUT,LOADING(null); now AwaitingGateArrival CLOSED/CARGO_HOLDING_TIMEOUT` |
| 上限未配置：装完到关卡腿建单不超过 25 秒（站点等待 10 秒；持货的话至少 40 秒） | FAIL | `<= 25 s` | `41.2 s` |
| 上限未配置：这一趟发给车的快照没有一张带持货期限，状态只有 LOADING 与 CLOSED/PLANNED_LOADING_COMPLETE；旅程走完 | FAIL | `LOADING, CLOSED/PLANNED_LOADING_COMPLETE; no deadline; Completed` | `CARGO_HOLDING_WAIT, CLOSED/CARGO_HOLDING_TIMEOUT, LOADING; 1 with deadline; Completed CLOSED/CARGO_HOLDING_TIMEOUT` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
