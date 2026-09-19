# L2 场景证据：catalog-change-binding-hold

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T070638033Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `d223b14eaca3e4fa6d02cc370a96848dd8b3a149` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T070638033Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 230 只改名：STAGING_TO_WIRE 出现来源目录变化、原因站点改名的暂停，WIRE_TO_GATE 没有暂停 | PASS | `(none) → STAGING_TO_WIRE/CATALOG_CHANGE/STATION_RENAMED` | `(none) → STAGING_TO_WIRE/CATALOG_CHANGE/STATION_RENAMED` |
| 改名之后绑定内容一字不改：生效版本、站点 id 与记下的旧名称都不变 | PASS | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` |
| STAGING_TO_WIRE 因目录变化暂停期间，WIRE_TO_GATE 需求照常受理并建出关卡单（不连带） | PASS | `ACCEPTED or DEMAND_ALREADY_ACCEPTED / TO_GATE CONFIRMED` | `ACCEPTED / TO_GATE CONFIRMED` |
| 210 删掉：WIRE_TO_GATE 出现来源目录变化、原因站点已不在目录的暂停 | PASS | `WIRE_TO_GATE/CATALOG_CHANGE/STATION_NOT_IN_CATALOG` | `WIRE_TO_GATE/CATALOG_CHANGE/STATION_NOT_IN_CATALOG` |
| 此前已建的关卡单不被取消、不改单：同一个 UpperId 与 OrderId，仍是 CONFIRMED | PASS | `W2G-35142615-d0d6-479c-bb69-05073de90aaa-GATE-1 / ORDER-000002 / CONFIRMED` | `W2G-35142615-d0d6-479c-bb69-05073de90aaa-GATE-1 / ORDER-000002 / CONFIRMED` |
| 已建关卡单的那一趟照常走完 | PASS | `Completed` | `Completed` |
| 车空闲时，关卡已不在目录的 WIRE_TO_GATE 新需求不受理 | PASS | `no journey / not ACCEPTED` | `no journey / TASK_TYPE_BINDING_STATION_NOT_IN_CATALOG` |
| 同一变化跑过多轮仍只有两条暂停；目录变化记录按站点记下改名与删除，各指向自己那条暂停 | PASS | `2 holds; 230 RENAMED, 210 REMOVED, each with a hold` | `STAGING_TO_WIRE/CATALOG_CHANGE/STATION_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/STATION_NOT_IN_CATALOG; changes: 230 RENAMED, 210 REMOVED, 230 RENAMED` |
| 看板显示两条暂停及其来源：STAGING_TO_WIRE 目录变化／站点改名，WIRE_TO_GATE 目录变化／站点已不在目录 | PASS | `STAGING_TO_WIRE: 已暂停 目录变化 站点改名；WIRE_TO_GATE: 已暂停 目录变化 站点已不在目录` | `STAGING_TO_WIRE 在本图需求集合 230 派工待送取货 已暂停 目录变化，自 2026-09-19T07:08:08.7003398+00:00，站点改名 暂停 \| WIRE_TO_GATE 在本图需求集合 210 关卡 已暂停 目录变化，自 2026-09-19T07:08:46.7314908+00:00，站点已不在目录 暂停` |
| 目录变化不自动改写绑定：场景结束时生效绑定集与开始时相同，没有按名称重绑 | PASS | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
