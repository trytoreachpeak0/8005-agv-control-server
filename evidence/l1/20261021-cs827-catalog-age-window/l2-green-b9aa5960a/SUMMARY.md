# L2 场景证据：catalog-age-window

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T083016203Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `b9aa5960adc5619566ee7b8b2de0680c9802fa05` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261021T083016203Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 首次读到在 40 分钟前的 STALE，车空着也不派：积压原因 DEMAND_OLDER_THAN_CATALOG_WINDOW，没有旅程、没有订单意图（REQ-0366） | PASS | `DEMAND_OLDER_THAN_CATALOG_WINDOW / not accepted / 0 journeys / 0 intents` | `DEMAND_OLDER_THAN_CATALOG_WINDOW / AcceptedAt='' / 0 journeys / 0 intents` |
| /api/dashboard/dispatch-backlog 列出 STALE，原因码 DEMAND_OLDER_THAN_CATALOG_WINDOW 带中文说明（仍在等车列表里，不像离开目录那样消失） | PASS | `one row / DEMAND_OLDER_THAN_CATALOG_WINDOW / Chinese description` | `DEMAND_OLDER_THAN_CATALOG_WINDOW / MES 接入首次读到这条需求已超过目录时间窗（默认 30 分钟），不再派车；需求仍在目录里，MES 字段更新也不会放回` |
| 首次读到在 1 分钟前的 FRESH 被受理并送达（旅程 Completed） | PASS | `Completed` | `Completed` |
| FRESH 送达、车又空了之后，STALE 被重新判过，仍未受理、原因不变；全程只有 FRESH 一趟旅程 | PASS | `re-judged after 2026-10-21T08:31:18.3766756+00:00 / not accepted / DEMAND_OLDER_THAN_CATALOG_WINDOW / 1 journey` | `LastSeenAt 2026-10-21 08:31:19.2154926+00:00 / AcceptedAt '' / DEMAND_OLDER_THAN_CATALOG_WINDOW / 1 journeys` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
