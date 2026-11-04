# L2 场景证据：load-result-while-not-ready-not-replayed

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261025T021803995Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `47cb1b8832729ddac7ba500b8fa07f52dbbdf82c` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `6.0.0` |
| protocolReleaseIdentity.tag | `protocol-v6.0.0` |
| protocolReleaseIdentity.commit | `98f172642937994ed01668d81fe41e69cf089e10` |
| protocolReleaseIdentity.protocolVersion | `7` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a52876be2ddcc9b8cdd1f1e3b7b7ddbe70d31a307f1d3f017d7a1eb95ffd77b1` |
| protocolReleaseIdentity.schemaBundleSha256 | `0dab682174e2d3e4f931b3c0e87c7fa55bd2cb3395356d7845e595d8bfb1c795` |
| protocolReleaseIdentity.vectorsSha256 | `e06ca9ea1d4445cd7d70fc190b0cf606a8e33cb7760d848c0270df24e2594c91` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261025T021803995Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：装货命令已下发（发件箱恰好一条），旅程在等结果 | PASS | `AwaitingLoadResult, 1 SlotOperationCommand` | `AwaitingLoadResult, 1 SlotOperationCommand` |
| 前置：车报不能出发之后，会话离开 Ready | PASS | `not Ready` | `RecoveryRequired/DEPARTURE_SAFETY_NOT_READY` |
| 前置：会话不就绪期间的装货结果被采信（Committed），之后会话回到 Ready | PASS | `Committed, Ready` | `Committed, Ready/READY` |
| 会话回到 Ready 之后车没有再收到这条装货命令（同一 messageId） | PASS | `0` | `0` |
| 发件箱里这条装货命令已确认（不会留到换协议身份时挡启动） | PASS | `AcknowledgedAt set` | `AcknowledgedAt=2026-10-25 02:18:47.4202882+00:00 FencedAt=` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
