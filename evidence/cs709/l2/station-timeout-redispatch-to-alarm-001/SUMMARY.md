# L2 场景证据：station-timeout-redispatch-to-alarm

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261014T134024038Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261014T134024038Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 第 1 趟期限到：旅程以 RELEASED_AT_STATION_DEADLINE 收尾，需求仍 Accepted、放回 1 次、不抑制、不告警 | PASS | `RELEASED_AT_STATION_DEADLINE / Accepted / 放回 1 次 / 抑制 0 / 告警 []` | `RELEASED_AT_STATION_DEADLINE / Accepted / 放回 1 次 / 归属 1 行 / 抑制 0 / 告警 []` |
| 第 2 趟期限到：旅程以 RELEASED_AT_STATION_DEADLINE 收尾，需求仍 Accepted、放回 2 次、不抑制、不告警 | PASS | `RELEASED_AT_STATION_DEADLINE / Accepted / 放回 2 次 / 抑制 0 / 告警 []` | `RELEASED_AT_STATION_DEADLINE / Accepted / 放回 2 次 / 归属 2 行 / 抑制 0 / 告警 []` |
| 第 3 趟期限到（已放回 2 次）：CANCELLED_BY_STATION_TIMEOUT、需求 Cancelled、抑制 1 条、挂人工搬运告警 | PASS | `CANCELLED_BY_STATION_TIMEOUT / Cancelled / 放回 2 次 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED]` | `CANCELLED_BY_STATION_TIMEOUT / Cancelled / 放回 2 次 / 归属 3 行 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED]` |
| 目录仍列出这一单：不再派车（3 条旅程、3 张取货单），告警仍挂着 | PASS | `目录列出 / 3 / 3 / 告警未清` | `目录列出 1 / 3 / 3 / Cancelled / 放回 2 次 / 归属 3 行 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED]` |
| 需求离开 MES 目录之后，人工搬运告警被清 | PASS | `告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED/cleared]` | `Cancelled / 放回 2 次 / 归属 3 行 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED/cleared]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
