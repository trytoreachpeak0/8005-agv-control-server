# L2 场景证据：charging-no-progress-isolates

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261020T224635911Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `83ca7e2025cf870ab1c6a5c645403790588cc4da` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261020T224635911Z-slot5` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 观察窗口起点开窗之后不被新样本重置：形成时周期上的起点仍是开窗那一刻（2026-10-20 22:46:53.7425984+00:00），形成时刻距起点不少于窗口 20 秒 | PASS | `window start 2026-10-20 22:46:53.7425984+00:00 / >= 20 s` | `window start 2026-10-20 22:46:53.7425984+00:00 / 21 s` |
| 一直报 CHARGING、电量不涨：窗口满后桩的分配暂停与车的充电资格暂停都写下（NO_PROGRESS_CONFIRMED，根因 UNKNOWN），周期进清桩中，用途转 CLEARING_MAINTENANCE，211 仍是这一趟的占用 | FAIL | `NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261020T224645746Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261020T224645746Z \| journey AwaitingPickupArrival CHARGING_NO_PROGRESS_CONFIRMED` | `NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED \| CLEARING UNABLE_TO_CHARGE \| claim CLEARING_MAINTENANCE charging:BROKERX-L2-0001:20261020T224645746Z \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261020T224645746Z \| journey AwaitingPickupArrival CHARGING_CLEARING_VEHICLE_STILL_CHARGING` |
| 形成之后另等十秒：RIoT 上一直只有那一张充电单，命令审计 0 条，211 一直是这一趟的占用，两侧各只有一条暂停 | PASS | `W2G-CHARGE-09c3783f-20261020T224645746Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261020T224645746Z \| NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED` | `W2G-CHARGE-09c3783f-20261020T224645746Z \| 0 commands \| OCCUPIED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261020T224645746Z \| NO_PROGRESS_CONFIRMED UNKNOWN \| NO_PROGRESS_CONFIRMED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
