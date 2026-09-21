# L2 场景证据：en-route-append-delay-gate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T172711509Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `cb5ae5a27eff05aa0eec914dd87cae58e9e04694` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260920T172711509Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 本区配了途中追加上限，值是一毫米：第一道门（本区未配置）过得去 | PASS | `1` | `1` |
| 第一条需求被一辆空闲车接走，车已在途 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 第二条需求被延迟门禁挡住（EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED），而不是被判成本区未配置 | PASS | `EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED` | `EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED` |
| 那趟旅程仍然只带着一条需求：追加一次都没有发生 | PASS | `1` | `1` |
| 停靠仍然是两个：计划一个字都没被改写 | PASS | `2` | `2` |
| 第二条需求没有被受理过：积压行上没有受理时刻，它在等下一辆空闲车 | PASS | `(无受理时刻)` | `(无受理时刻)` |
| 计划只发过一版：没有追加，就没有整体重发 | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
