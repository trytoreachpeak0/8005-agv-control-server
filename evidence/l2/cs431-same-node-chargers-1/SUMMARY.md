# L2 场景证据：charging-registry-emptied-degrades-and-resumes

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261001T051047865Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `15237780c6c13ddf75e7ef1e0883d6ec4f956f8c` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261001T051047865Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 开窗（名册登记 211）后，电量低于强制充电线的 A 取得 CHARGING 用途占有与 211 的预占，充电单在 RIoT 上恰好一张；周期与预占记下的是这一版名册 | PASS | `OK / 1 / claim[CHARGING charging:BROKERX-L2-0001:20261001T051145506Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261001T051145506Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261001T051145506Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z]` | `OK / 1 / claim[CHARGING charging:BROKERX-L2-0001:20261001T051145506Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261001T051145506Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261001T051145506Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z]` |
| 关窗是同一个动词导入空名册：成一个新的空版本，并指出还有充电在进行、窗口还不能关 | PASS | `OK / 0 entries / emptyRoster / version > 1 / windowCanClose false` | `OK / 0 entries / emptyRoster=True / v2 / windowCanClose False` |
| 名册为空时需要充电的 B 进入服务端持有的人工充电等待（ROSTER_EMPTY），告警恰好一次，车载端收到并确认 manualChargingHold=true；B 没有用途占有、预占、周期或建单 | PASS | `ROSTER_EMPTY warned \| 1 warnings \| true:none:acked / claim[] station[] cycle[] riot[]` | `ROSTER_EMPTY warned \| 1 warnings \| true:none:acked / claim[] station[] cycle[] riot[]` |
| 名册重新启用（211、213，213 空着）之后，仍在等待中的 B 十几轮里不被分配、等待不被解除，告警与快照也不重复 | PASS | `OK / 2 / ROSTER_EMPTY \| claim[] station[] cycle[] riot[] \| 1 warnings \| true:none:acked` | `OK / 2 / ROSTER_EMPTY \| claim[] station[] cycle[] riot[] \| 1 warnings \| true:none:acked` |
| 「充电后返回服务」受理时同一次保存解除等待（经过记录写上这个请求 id）；B 收到并确认 manualChargingHold=false，随后取得空着的 213 与 CHARGING 用途占有 | PASS | `RETURNED_TO_ELIGIBILITY_EVALUATION \| 0 holds \| released by 78e2b89b-595d-43c4-b6b4-fa43a3b43666 / claim[CHARGING charging:BROKERX-L2-0002:20261001T051207494Z] station[213 RESERVED CHARGER charging:BROKERX-L2-0002:20261001T051207494Z v3] cycle[charging-cycle:BROKERX-L2-0002:20261001T051207494Z 213 v3 ACTIVE W2G-CHARGE-BROKERX-L2-0002-20261001T051207494Z] riot[W2G-CHARGE-BROKERX-L2-0002-20261001T051207494Z] / true:none:acked false:none:acked ...` | `RETURNED_TO_ELIGIBILITY_EVALUATION \| 0 holds \| released by 78e2b89b-595d-43c4-b6b4-fa43a3b43666 / claim[CHARGING charging:BROKERX-L2-0002:20261001T051207494Z] station[213 RESERVED CHARGER charging:BROKERX-L2-0002:20261001T051207494Z v3] cycle[charging-cycle:BROKERX-L2-0002:20261001T051207494Z 213 v3 ACTIVE W2G-CHARGE-BROKERX-L2-0002-20261001T051207494Z] riot[W2G-CHARGE-BROKERX-L2-0002-20261001T051207494Z] / true:none:acked false:none:acked false:CHARGING:acked false:CHARGING:acked` |
| A 的周期在名册置空、重新启用的整段里原样继续：同一趟、同一个桩、同一个单号，仍按它记下的那一版名册，没有任何取消命令 | PASS | `claim[CHARGING charging:BROKERX-L2-0001:20261001T051145506Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261001T051145506Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261001T051145506Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z] \| 0 commands` | `claim[CHARGING charging:BROKERX-L2-0001:20261001T051145506Z] station[211 RESERVED CHARGER charging:BROKERX-L2-0001:20261001T051145506Z v1] cycle[charging-cycle:BROKERX-L2-0001:20261001T051145506Z 211 v1 ACTIVE W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z] riot[W2G-CHARGE-BROKERX-L2-0001-20261001T051145506Z] \| 0 commands` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
