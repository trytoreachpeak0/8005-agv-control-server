# L2 场景证据：staging-reservations-not-full

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T074516587Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `0fd2b4429dd1a3529ab0b8be22c6d17b416e5e42` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T074516587Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 甲、乙把两侧预留占满（各 4 篮）、车上 0 篮：五轮之后装货阶段不是 VEHICLE_FULL，没有开往卸货站的离站核验 | PASS | `B joined, 0 loaded, not VEHICLE_FULL, no check to the gate` | `B joined, 0 loaded, AwaitingSublot LOADING(null), checks to []` |
| 操作员扫码前取消甲：授权、车报的结果被确认，甲 Cancelled（FRONT 的预留放开） | PASS | `AUTHORIZED, acknowledged, Cancelled` | `AUTHORIZED, ack 10/21/2026 15:45:35, Cancelled` |
| 甲取消后 FRONT 有空，丙追加进了这一趟 | PASS | `journey:65b2f7eb-da57-4d8c-8902-d2bdd2471757` | `journey:65b2f7eb-da57-4d8c-8902-d2bdd2471757` |
| 乙、丙装上（车上 5 篮、FRONT 还空 3 格），没有等着的需求：五轮之后仍是 CARGO_HOLDING_WAIT，没有开往卸货站的离站核验 | PASS | `B, C LOADED, CARGO_HOLDING_WAIT, checks only to 派工待送取货` | `B LOADED, C LOADED, AwaitingStationDeparture CARGO_HOLDING_WAIT, checks to [派工待送取货, 派工待送取货]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
