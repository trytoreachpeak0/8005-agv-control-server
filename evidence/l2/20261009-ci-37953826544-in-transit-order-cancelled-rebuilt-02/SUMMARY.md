# L2 场景证据：in-transit-order-cancelled-rebuilt

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T161923217Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T161923217Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 订单被取消后旅程写 ORDER_ENDED_WITHOUT_ARRIVAL，仍停在开往取货站 | PASS | `AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL` | `AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL` |
| 延迟之内不释放、不改派、不重建：归属未移除，仍只有一趟旅程、一张移动意图，码与开始时刻不变 | PASS | `未移除 / 1 趟 / 1 张 / ORDER_ENDED_WITHOUT_ARRIVAL / 2026-10-09 16:19:50.72064+00:00` | `未移除 / 1 趟 / 1 张 / ORDER_ENDED_WITHOUT_ARRIVAL / 2026-10-09 16:19:50.72064+00:00` |
| 没有发出任何订单命令或急停（包括对已终结的单再发取消） | PASS | `0 / 0` | `0 / 0` |
| 延迟之后重建：取货停靠（同一个停靠、同一序位、同一站）改指向一张服务端自己的新单，意图已确认，需求与车都是原来那条、那辆，目标站不变；RIoT 上有这张单、指派给同一辆车 | PASS | `新单号 W2G-… / CONFIRMED / c97ec9fb-4775-4678-8a69-12ba50642731 / BROKERX-L2-0001 / 站 12 / 序位 1 / RIoT 1 张` | `W2G-c97ec9fb-4775-4678-8a69-12ba50642731-REBUILD-1-1 / CONFIRMED / c97ec9fb-4775-4678-8a69-12ba50642731 / BROKERX-L2-0001 / 站 12 / 序位 1 / RIoT 1 张` |
| 重建之后：仍是同一趟旅程、开往取货站，码清掉；恰好两张移动意图（旧单留作记录）、一条重建记录（REBUILT、来源 ORDER_CANCELLED_IN_RIOT）；仍没有任何订单命令 | PASS | `AwaitingPickupArrival / (无码) / 1 趟 / 2 张 / REBUILT ORDER_CANCELLED_IN_RIOT / 0 条命令` | `AwaitingPickupArrival / (无码) / 1 趟 / 2 张 / REBUILT ORDER_CANCELLED_IN_RIOT / 0 条命令` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
