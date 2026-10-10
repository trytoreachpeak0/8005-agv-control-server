# L2 场景证据：catalog-change-binding-hold

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160937956Z` |
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
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160937956Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 230 只改名：STAGING_TO_WIRE 出现来源目录变化、原因站点改名的暂停，WIRE_TO_GATE 没有暂停 | PASS | `(none) → STAGING_TO_WIRE/CATALOG_CHANGE/STATION_RENAMED` | `(none) → STAGING_TO_WIRE/CATALOG_CHANGE/STATION_RENAMED` |
| 改名之后绑定内容一字不改：生效版本、站点 id 与记下的旧名称都不变 | PASS | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` |
| STAGING_TO_WIRE 因目录变化暂停期间，WIRE_TO_GATE 需求照常受理并建出关卡单（不连带） | PASS | `ACCEPTED or DEMAND_ALREADY_ACCEPTED / TO_GATE CONFIRMED` | `DEMAND_ALREADY_ACCEPTED / TO_GATE CONFIRMED` |
| 210 删掉：WIRE_TO_GATE 出现来源目录变化、原因站点已不在目录的暂停 | PASS | `WIRE_TO_GATE/CATALOG_CHANGE/STATION_NOT_IN_CATALOG` | `WIRE_TO_GATE/CATALOG_CHANGE/STATION_NOT_IN_CATALOG` |
| 此前已建的关卡单不被取消、不改单：同一个 UpperId 与 OrderId，仍是 CONFIRMED | PASS | `W2G-5e517902-6a5d-46a0-9c49-0c3fe283e47a-GATE-1 / ORDER-000002 / CONFIRMED` | `W2G-5e517902-6a5d-46a0-9c49-0c3fe283e47a-GATE-1 / ORDER-000002 / CONFIRMED` |
| 已建关卡单的那一趟照常走完 | PASS | `Completed` | `Completed` |
| 车空闲时，关卡已不在目录的 WIRE_TO_GATE 新需求不受理，原因码恰好是 TASK_TYPE_BINDING_STATION_NOT_IN_CATALOG（本轮目录先挡住，还轮不到暂停） | PASS | `no journey / TASK_TYPE_BINDING_STATION_NOT_IN_CATALOG` | `no journey / TASK_TYPE_BINDING_STATION_NOT_IN_CATALOG` |
| 同一变化跑过多轮仍只有两条暂停；目录变化记录 230 恰好一行改名、210 恰好一行删除（删 210 换了修订也不重复记 230），各指向自己那条暂停 | PASS | `2 holds; changes: 230 RENAMED, 210 REMOVED, each with a hold` | `STAGING_TO_WIRE/CATALOG_CHANGE/STATION_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/STATION_NOT_IN_CATALOG; changes: 230 RENAMED, 210 REMOVED` |
| 看板显示两条暂停及其来源：STAGING_TO_WIRE 目录变化／站点改名，WIRE_TO_GATE 目录变化／站点已不在目录 | PASS | `STAGING_TO_WIRE: 已暂停 目录变化 站点改名；WIRE_TO_GATE: 已暂停 目录变化 站点已不在目录` | `STAGING_TO_WIRE 在本图需求集合 230 派工待送取货 已暂停 目录变化，自 2026-10-09T16:10:04.7550769+00:00，站点改名 暂停 \| WIRE_TO_GATE 在本图需求集合 210 关卡 已暂停 目录变化，自 2026-10-09T16:10:40.7750076+00:00，站点已不在目录 暂停` |
| 目录变化不自动改写绑定：场景结束时生效绑定集与开始时相同，没有按名称重绑 | PASS | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` | `v1 STAGING_TO_WIRE=230/派工待送取货/L2-SYNTHETIC-SITE-CHECK; v1 WIRE_TO_GATE=210/关卡/L2-SYNTHETIC-SITE-CHECK` |
| 210 以原名称放回目录、确认过几轮之后，等着的与新来的 WIRE_TO_GATE 需求都仍不受理，原因码 TASK_TYPE_HELD，那条暂停仍未解除（站点恢复不自动解暂停） | PASS | `second: no journey / TASK_TYPE_HELD; third: no journey / TASK_TYPE_HELD; hold bc48c6839a18496487e4cdbb2e5babc4 unreleased` | `second: no journey / TASK_TYPE_HELD; third: no journey / TASK_TYPE_HELD; hold bc48c6839a18496487e4cdbb2e5babc4` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
