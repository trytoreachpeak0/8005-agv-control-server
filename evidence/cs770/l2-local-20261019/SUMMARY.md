# L2 场景证据：own-order-cancelled-low-battery-charges

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261019T082558102Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d8d7c7928c1723bf3a08452d9089267756676263` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261019T082558102Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 到点不重建：重建记录停在 STOPPED / MANDATORY_CHARGE_REQUIRED（读数与线写在等待原因里）；这条需求仍只有一张移动意图，RIoT 上没有重建出来的单 | PASS | `STOPPED / MANDATORY_CHARGE_REQUIRED / BATTERY_25_BELOW_30 / 1 张意图 / RIoT 0 张` | `STOPPED / MANDATORY_CHARGE_REQUIRED / BATTERY_25_BELOW_30 / 1 张意图 / RIoT 0 张` |
| 需求放回：旅程 Completed / RELEASED_FOR_REDISPATCH，归属以 RELEASED_FOR_REDISPATCH 移除，需求仍 Accepted、不抑制；没有任何订单命令 | PASS | `Completed/RELEASED_FOR_REDISPATCH, RELEASED_FOR_REDISPATCH/Accepted, 抑制 0, 命令 0` | `Completed/RELEASED_FOR_REDISPATCH, RELEASED_FOR_REDISPATCH/Accepted, 抑制 0, 命令 0` |
| 车进了充电队：分到 211，周期 EN_ROUTE、充电单被 RIoT 确认，车持有这一趟的 CHARGING 用途；这辆车没有再被派往任何取货站 | PASS | `EN_ROUTE @211 confirmed / CHARGING <cycle journey> / 取货单 1 张` | `EN_ROUTE @211 confirmed '2026-10-19 08:29:11.0162048+00:00' / CHARGING charging:BROKERX-L2-0001:20261019T082908865Z / 取货单 1 张` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
