# L2 场景证据：load-cancelled-journey-continues-departs-early

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T100715453Z` |
| agvId | `AGV-L2-001` |
| batchId | `cs742` |
| controlServerCommit | `5830dd625727c85751c2b7d2f2213e3abd04d68b` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T100715453Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：乙进了甲那一趟旅程；随后导入的参数版本里本区途中追加上限为空，持货等单不适用 | PASS | `journey journey:47d7dcce-0965-4c8c-952e-498a6247e4f2, OK, (unconfigured)` | `journey journey:47d7dcce-0965-4c8c-952e-498a6247e4f2, OK, ''` |
| 12 号站对照：甲已装上，离站核验不早于这一站车上清单的离站期限（本站有 Loaded 时照旧等，#668） | PASS | `departure check at or after 2026-10-17T10:09:27.6330555+00:00` | `departure check 2026-10-17T10:09:28.6336174+00:00; worklists 1x1@2026-10-17T10:09:25.6597592+00:00 2x0@2026-10-17T10:09:27.6330555+00:00` |
| 取消走完：授权 AUTHORIZED、ALL_EMPTY 结果被确认；乙 TERMINATED，甲仍 LOADED | PASS | `AUTHORIZED / ALL_EMPTY acknowledged / B TERMINATED, A LOADED` | `AUTHORIZED / ALL_EMPTY at 10/17/2026 18:09:30 / B TERMINATED, A LOADED` |
| 11 号站本站空了、一篮没装：离站核验早于这一站的离站期限发出，车不再白等到期限（#742） | FAIL | `departure check before 2026-10-17T10:10:59.6412356+00:00` | `departure check 2026-10-17T10:11:00.6365845+00:00` |
| 车上最新那一版 11 号站清单不带晚于离站核验的期限：车走时屏上不剩倒计时（判定与下发同一个期限） | PASS | `latest empty, deadline none or <= 2026-10-17T10:11:00.6365845+00:00` | `3x1@2026-10-17T10:10:59.6412356+00:00 4x0@(none) 5x0@2026-10-17T10:10:59.6412356+00:00` |
| 车开往下一站（关卡）照常卸完甲，旅程收尾 | PASS | `Completed` | `Completed CLOSED/PLANNED_LOADING_COMPLETE` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
