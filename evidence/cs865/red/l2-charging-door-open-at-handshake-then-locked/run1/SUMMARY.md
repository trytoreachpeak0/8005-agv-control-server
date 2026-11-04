# L2 场景证据：charging-door-open-at-handshake-then-locked

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261025T022224113Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `8f93cd65622193f68e00233177fdc151337940a3` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261025T022224113Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：服务端收到的握手快照里 1 号仓是 UNLOCKED（会话在仓门开着时建立） | PASS | `<version> UNLOCKED` | `1 UNLOCKED` |
| 电量 25（低于强制线 30）、1 号仓开着：另等二十秒，一直没有充电周期（门锁没证明仍不出发） | PASS | `0` | `0` |
| 1 号仓锁上（SafetyStateChanged 不带逐仓状态）：服务端要来一张新快照、1 号仓 LOCKED，随后分到 211、开往 211 的单已确认 | FAIL | `EN_ROUTE 211 ordered \| <version> LOCKED` | `(none) \| 1 UNLOCKED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
