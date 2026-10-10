# L2 场景证据：charging-registry-emptied-degrades-and-resumes

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161816787Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-9` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161816787Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 开窗（名册登记 211）后，电量低于强制充电线的 A 取得 CHARGING 用途占有与 211 的预占，充电单在 RIoT 上恰好一张；周期与预占记下的是这一版名册 | PASS | `OK / 1 / claim[CHARGING charging:BROKERX-L2-0001:20261009T161848874Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261009T161848874Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261009T161848874Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z]` | `OK / 1 / claim[CHARGING charging:BROKERX-L2-0001:20261009T161848874Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261009T161848874Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261009T161848874Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z]` |
| 关窗是同一个动词导入空名册：成一个新的空版本，并指出还有充电在进行、窗口还不能关 | PASS | `OK / 0 entries / emptyRoster / version > 1 / windowCanClose false` | `OK / 0 entries / emptyRoster=True / v2 / windowCanClose False` |
| 名册为空时需要充电的 B 进入服务端持有的人工充电等待（ROSTER_EMPTY），告警恰好一次，车载端收到并确认 manualChargingHold=true；B 没有用途占有、预占、周期或建单 | PASS | `ROSTER_EMPTY warned \| 1 warnings \| true:none:acked / claim[] station[] cycle[] riot[]` | `ROSTER_EMPTY warned \| 1 warnings \| true:none:acked / claim[] station[] cycle[] riot[]` |
| 名册重新启用（211、213，213 空着）之后，仍在等待中的 B 十几轮里不被分配、等待不被解除，告警与快照也不重复 | PASS | `OK / 2 / ROSTER_EMPTY \| claim[] station[] cycle[] riot[] \| 1 warnings \| true:none:acked` | `OK / 2 / ROSTER_EMPTY \| claim[] station[] cycle[] riot[] \| 1 warnings \| true:none:acked` |
| 「充电后返回服务」受理时同一次保存解除等待（经过记录写上这个请求 id）；B 收到并确认 manualChargingHold=false，随后取得空着的 213 与 CHARGING 用途占有 | PASS | `RETURNED_TO_ELIGIBILITY_EVALUATION \| 0 holds \| released by 87bf4959-10d6-4a97-8c15-300d14ef2f2c / claim[CHARGING charging:BROKERX-L2-0002:20261009T161910678Z] station[213 RESERVED CHARGER charging:BROKERX-L2-0002:20261009T161910678Z v3] cycle[charging-cycle:BROKERX-L2-0002:20261009T161910678Z 213 v3 ACTIVE W2G-CHARGE-BROKERX-L2-0002-20261009T161910678Z] riot[W2G-CHARGE-BROKERX-L2-0002-20261009T161910678Z] / true:none:acked false:none:acked ...` | `RETURNED_TO_ELIGIBILITY_EVALUATION \| 0 holds \| released by 87bf4959-10d6-4a97-8c15-300d14ef2f2c / claim[CHARGING charging:BROKERX-L2-0002:20261009T161910678Z] station[213 RESERVED CHARGER charging:BROKERX-L2-0002:20261009T161910678Z v3] cycle[charging-cycle:BROKERX-L2-0002:20261009T161910678Z 213 v3 ACTIVE W2G-CHARGE-BROKERX-L2-0002-20261009T161910678Z] riot[W2G-CHARGE-BROKERX-L2-0002-20261009T161910678Z] / true:none:acked false:none:acked false:CHARGING:acked false:CHARGING:acked` |
| A 的周期在名册置空、重新启用的整段里原样继续：同一趟、同一个桩、同一个单号，仍按它记下的那一版名册，没有任何取消命令 | PASS | `claim[CHARGING charging:BROKERX-L2-0001:20261009T161848874Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261009T161848874Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261009T161848874Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z] \| 0 commands` | `claim[CHARGING charging:BROKERX-L2-0001:20261009T161848874Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261009T161848874Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261009T161848874Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261009T161848874Z] \| 0 commands` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
