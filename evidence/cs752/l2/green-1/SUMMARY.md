# L2 场景证据：staging-only-vehicle-returns-to-staging-origin

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T135823476Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T135823476Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 只做待送的车开局空闲：一台（A）的空闲返回单开往待送点 305，305 被 A 以 FIXED_TASK_STATION 预占；另一台（B）这一次回落到等待点，以 WAITING_POINT 预占 | PASS | `A -> 305 FIXED_TASK_STATION RESERVED / B -> 214\|215 WAITING_POINT` | `A=BROKERX-L2-0001 -> 305 BROKERX-L2-0001 FIXED_TASK_STATION RESERVED / B=BROKERX-L2-0002 -> 214 BROKERX-L2-0002 WAITING_POINT` |
| A 到站：旅程 Completed，305 转为 A 的 FIXED_TASK_STATION 占用，A 的 IDLE_RETURN 用途已释放，A 在 305 | PASS | `Completed / 305 OCCUPIED by A / no claim / at 305` | `Completed / OCCUPIED by BROKERX-L2-0001 FIXED_TASK_STATION / claim  / at 305` |
| A 在 305 停了十几轮：没有新的 IDLE_RETURN 用途、没有新的空闲返回单，车仍在 305；看板结论码 IDLE_RETURN_HELD_AT_STAGING_ORIGIN | PASS | `no claim / 1 intents / at 305 / IDLE_RETURN_HELD_AT_STAGING_ORIGIN` | `claim  / 1 intents / at 305 / IDLE_RETURN_HELD_AT_STAGING_ORIGIN` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
