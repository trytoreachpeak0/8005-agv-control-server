# L2 场景证据：charging-one-charger-three-vehicles-contend

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261002T154346859Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `6b7b3e38b7d72b454b227a104de2e37a3198db86` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002, AGV-L2-003/BROKERX-L2-0003` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37023804697-1\_stage\l2-20261002T154346859Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：名册经 FieldOps 导入一个桩（211）；三台车电量都在默认强制充电线之上时，没有任何充电承诺、建单或人工充电等待 | PASS | `OK / 1 / claims[] chargers[] cycles[] intents[] riot[] commands[0] / 0 holds` | `OK / 1 / claims[] chargers[] cycles[] intents[] riot[] commands[0] / 0 holds` |
| 三台车同时需要充电：恰好一台取得 CHARGING 用途占有与 211 的预占（另等十几轮之后仍是一台），它是电量最低的那台；它的充电单（move + act，形态 CHARGE）在 RIoT 上恰好一张，单号是周期派生的那个 | PASS | `1 / claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 RESERVED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0] / policy v2 / roster v1` | `1 / claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 RESERVED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0] / policy v2 / roster v1` |
| 另两台留在队里：各自报出「211 已被预占」，没有用途占有、站点独占、人工充电等待或订单意图 | PASS | `reported/reported / 0 claims, 0 stations, 0 holds, 0 intents` | `reported/reported / 0 claims, 0 stations, 0 holds, 0 intents` |
| 充电单完成后，在 211 上 docked 的正是预占它的那台车；服务端判到桩，211 由预占转为这一趟的占用，承诺的其余部分逐字不变 | PASS | `BROKERX-L2-0003 / claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 OCCUPIED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0]` | `BROKERX-L2-0003 / claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 OCCUPIED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0]` |
| 队里的 AGV-L2-001 电量降到 10%（比已预占那台的 40% 更低）之后报出的仍是「211 已被预占」；那一刻已预占那台的用途占有、预占、周期与单号逐字未变，没有第二张单，也没有任何取消命令 | PASS | `reported \| claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 OCCUPIED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0]` | `reported \| claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 OCCUPIED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0]` |
| 此后十几轮里预占没有易手；整段里 211 上 docked 的车从没超过 1 辆 | PASS | `BROKERX-L2-0003 \| claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 OCCUPIED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0] / at most 1 docked` | `BROKERX-L2-0003 \| claims[BROKERX-L2-0003=charging:BROKERX-L2-0003:20261002T154427634Z] chargers[211 OCCUPIED BROKERX-L2-0003 charging:BROKERX-L2-0003:20261002T154427634Z] cycles[BROKERX-L2-0003 charging-cycle:BROKERX-L2-0003:20261002T154427634Z 211 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] intents[BROKERX-L2-0003 W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z CHARGE 211] riot[W2G-CHARGE-BROKERX-L2-0003-20261002T154427634Z] commands[0] / at most 1 docked` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
