# L2 场景证据：normal-load

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261020T180231067Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `77b2229e80bec72c224c4c46baa6781b1f7f7bc9` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261020T180231067Z-slot5` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 受理后建出 TO_PICKUP 单并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| 候选判定结果是受理：积压行记着这一次受理的 AcceptedAt，原因码是受理或其后一轮写的「已受理」 | PASS | `ACCEPTED (or DEMAND_ALREADY_ACCEPTED written after AcceptedAt) / AcceptedAt set and = AcceptedDemands.AcceptedAt` | `ReasonCode=ACCEPTED / AcceptedAt=2026-10-20 18:02:59.2501927+00:00 / LastSeenAt=2026-10-20 18:02:59.2501927+00:00 / AcceptedDemands.AcceptedAt=2026-10-20 18:02:59.2501927+00:00` |
| 车在路上时不采信到站：单已报成功、位置已报到取货点，只有运动证据说车还没停稳，运行时转过两轮之后仍然不前进 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 装载操作提交（Committed） | PASS | `Committed` | `Committed` |
| 出发前安全检查通过后才建 TO_GATE 单 | PASS | `CONFIRMED` | `CONFIRMED` |
| journey 走到 Completed | PASS | `Completed` | `Completed` |
| 卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| 需求终态为 Succeeded | PASS | `Succeeded` | `Succeeded` |
| 全程只建了两条 RIoT 单（取货一条、关卡一条） | PASS | `2` | `2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
