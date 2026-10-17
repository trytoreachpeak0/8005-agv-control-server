# L2 场景证据：staging-low-battery-nothing-loaded-charges

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T140226470Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `219687e70afad4f4f097b7e828a0a3a6762dd536` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `4e9f3df8fb1733311e1ce534c1129cfb904760a4` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `743b886d99194038b893e98aed2a1dd01409cd7f80d39c734e02d860e0e5827c` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T140226470Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 待送点缺电、一篮没装：旅程原地收尾（STAGING_CHARGING_REQUIRED），空清单说 CHARGING_REQUIRED；需求放回、仍 Accepted、不抑制 | PASS | `Completed/STAGING_CHARGING_REQUIRED, CHARGING_REQUIRED, RELEASED_FOR_REDISPATCH/Accepted, 抑制 0` | `Completed/STAGING_CHARGING_REQUIRED, [,CHARGING_REQUIRED], RELEASED_FOR_REDISPATCH/Accepted, 抑制 0` |
| 从压电量之前到收尾之后几轮，V 不空闲返回（没有新的开往等待点的单、没有 IDLE_RETURN 用途），放回的需求不派给缺电的 V（对 cs#712 无判别力，见开头） | PASS | `开往等待点的单 1 张不变 / 无 IDLE_RETURN / 没再派给 V` | `开往等待点的单 1 → 1 / 用途 CHARGING charging:BROKERX-L2-0001:20261017T140308770Z / 再派给 V 0 次` |
| V 进了充电队：分到 211，周期 EN_ROUTE、充电单被 RIoT 确认，V 持有这一趟的 CHARGING 用途 | PASS | `EN_ROUTE @211 confirmed / CHARGING <cycle journey>` | `EN_ROUTE @211 upperId 'W2G-CHARGE-09c3783f-20261017T140308770Z' confirmed '2026-10-17 14:03:10.1468579+00:00' / CHARGING charging:BROKERX-L2-0001:20261017T140308770Z` |
| 充电单跑完：V 停在 211 上 CHARGING，211 是这一趟的占用 | PASS | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261017T140308770Z \| at 211` | `CHARGING \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261017T140308770Z \| at 211` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
