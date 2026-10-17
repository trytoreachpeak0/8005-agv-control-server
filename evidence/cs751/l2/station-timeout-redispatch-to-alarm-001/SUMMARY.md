# L2 场景证据：station-timeout-redispatch-to-alarm

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261017T214859299Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `28d113dd023e892a40116ba0a888c52f1eff493d` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `13eca82ac199bd97c1ad49d036c70cb5b012250f` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `128daae52758749d4d6f346231fdb336ca5d1079773bcab5430c20c53ce6b10b` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261017T214859299Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 第 1 趟期限到（出厂上限 0）：CANCELLED_BY_STATION_TIMEOUT、需求 Cancelled、不放回、抑制 1 条、挂人工搬运告警 | PASS | `CANCELLED_BY_STATION_TIMEOUT / Cancelled / 放回 0 次 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED]` | `CANCELLED_BY_STATION_TIMEOUT / Cancelled / 放回 0 次 / 归属 1 行 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED]` |
| 看板站点超时抑制清单列出这条需求：批号、取货站点、终结时刻 | PASS | `L2-STR-20261017T214859299Z / N1-3_N1-7 / 有终结时刻` | `L2-STR-20261017T214859299Z / N1-3_N1-7 / 2026-10-17T21:50:27.6808814+00:00` |
| 目录仍列出这一单：不再派车（1 条旅程、1 张取货单），告警仍挂着 | PASS | `目录列出 / 1 / 1 / 告警未清` | `目录列出 1 / 1 / 1 / Cancelled / 放回 0 次 / 归属 1 行 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED]` |
| 需求离开 MES 目录之后，人工搬运告警被清 | PASS | `告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED/cleared]` | `Cancelled / 放回 0 次 / 归属 1 行 / 抑制 1 / 告警 [STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED/cleared]` |
| 需求离开 MES 目录之后，站点超时抑制清单不再列出它 | PASS | `(not listed)` | `(not listed)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
