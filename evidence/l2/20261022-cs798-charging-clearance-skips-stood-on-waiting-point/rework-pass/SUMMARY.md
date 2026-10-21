# L2 场景证据：charging-clearance-skips-stood-on-waiting-point

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T173912483Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `56300ba1b6b105d61e6f2fe42a8aebbcd6d1b8e0` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T173912483Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| A 在 211 上充不上，周期清桩中；B 接了搬运需求、旅程在途，停在 214 上（RIoT 报的当前站），214 没有占用行 | PASS | `CLEARING / AGV-L2-002 AwaitingPickupArrival / 214 (none)` | `CLEARING / AGV-L2-002 AwaitingPickupArrival VEHICLE_WAITING_AT_CHECKPOINT / 214 (none)` |
| 旧单结束、B 站在 214 上（没有占用行）、215 不可达：A 原地排队（CHARGING_CLEARANCE_NO_WAITING_POINT），另等十秒没有清桩移动意图、214 一直没有占用行；告警里 214 的逐点原因是 WAITING_POINT_STOOD_ON(AGV-L2-002,read) | PASS | `AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT \| 0 clearance intents \| 214 (none) / 214=WAITING_POINT_STOOD_ON(AGV-L2-002,read)` | `AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT \| 0 clearance intents \| 214 (none) / CHARGING_CLEARANCE_NO_WAITING_POINT: vehicle BROKERX-L2-0001 (journey charging:BROKERX-L2-0001:20261021T173935203Z) may leave its charger for a waiting point, but none is eligible (214=WAITING_POINT_STOOD_ON(AGV-L2-002,read), 215=WAITING_POINT_UNREACHABLE). It stays where it is and no other station is guessed (REQ-0178); a manual station clearance still completes it.` |
| B 离开 214（回到 210，旅程照旧在途）：A 预占 214（点上没车、没有占用行时照常派，不误伤） | PASS | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T173935203Z` | `RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261021T173935203Z` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
