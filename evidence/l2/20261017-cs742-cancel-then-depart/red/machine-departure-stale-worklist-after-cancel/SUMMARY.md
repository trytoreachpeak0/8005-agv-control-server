# L2 场景证据：machine-departure-stale-worklist-after-cancel

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T101440474Z` |
| agvId | `AGV-L2-001` |
| batchId | `cs742` |
| controlServerCommit | `60561a7f55e5383167ce6cf8eafdfe256a13a956` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `4.0.0` |
| protocolReleaseIdentity.tag | `protocol-v4.0.0` |
| protocolReleaseIdentity.commit | `3fb214e57d861a909d872511940d26133eb32dca` |
| protocolReleaseIdentity.protocolVersion | `5` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a` |
| protocolReleaseIdentity.schemaBundleSha256 | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T101440474Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：乙、丁进了甲那一趟旅程，并进同一个 11 号站停靠 | PASS | `B and D on one stop` | `B journey:758b45e5-4f3f-4ec6-a152-3762c00da1ff\|PICKUP, D journey:758b45e5-4f3f-4ec6-a152-3762c00da1ff\|PICKUP` |
| 丁装上、乙终结；取消那一版之后服务端又发了一版本停靠清单（号更大），两版都没有待做项 | PASS | `D LOADED, B TERMINATED, two empty worklists` | `B TERMINATED; worklists 3x2@2026-10-17T10:18:23.5983272+00:00 4x1@2026-10-17T10:18:25.6007476+00:00 5x0@(none) 6x0@2026-10-17T10:18:26.6000505+00:00` |
| 出发请求带的是取消那一版的号（比当前号小），新旧两版没有实质差别，答 ACCEPTED | FAIL | `ACCEPTED for revision 5` | `REJECTED WORKLIST_REVISION_STALE for revision 5, current 6` |
| 受理之后离站核验发出，早于这一站的离站期限 | FAIL | `departure check before 2026-10-17T10:18:26.6000505+00:00` | `departure check (none)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
