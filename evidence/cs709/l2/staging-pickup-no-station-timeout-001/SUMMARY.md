# L2 场景证据：staging-pickup-no-station-timeout

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261014T133913331Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `2b25b45be5427b32392fdab5e5140467fa5557d9` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261014T133913331Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 需求派向派工待送站 305 | PASS | `pickup 305` | `pickup 305` |
| 待送点的清单不带离站期限（stationDepartureDeadlineAt 为空） | PASS | `至少 1 版清单，期限全为空` | `清单 1 版，带期限的 []` |
| 三个离站期限过去：本站不结束、需求不放回不终结、不抑制、不告警 | PASS | `AwaitingSublot / Accepted / 旅程 1 条 / 放回 0 次 / 抑制 0 / 告警 0` | `AwaitingSublot / Accepted / 旅程 1 条 / 放回 0 次 / 抑制 0 / 告警 0` |
| 车辆业务状态快照里没有持货期限（cargoHoldingDeadlineAt 为空） | PASS | `带持货期限的 0 张` | `带持货期限的 []` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
