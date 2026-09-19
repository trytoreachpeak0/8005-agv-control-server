# L2 场景证据：binding-hold-dashboard-not-cascading

结论：**FAIL**

失败原因：Timed out after 90s waiting for: demand first was accepted and dispatched to the pickup station. Last observed: (nothing)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T071033426Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `72877d990227a7f257a0e32b708b7f9c431c9334` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T071033426Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 确认页不自动刷新，带着这一行的 Map 与任务类型；提交后 303 回到看板主页 | PASS | `no refresh, taskType=STAGING_TO_WIRE, 303 → /` | `refresh=False, 303 → /` |
| 暂停只落在 Map 25 的 STAGING_TO_WIRE 上，来源看板人工；WIRE_TO_GATE 没有暂停 | FAIL | `STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD` | `WIRE_TO_NITROGEN/MANUAL/DASHBOARD_MANUAL_HOLD, WIRE_TO_OPTICAL/MANUAL/DASHBOARD_MANUAL_HOLD, DIE_TO_OVEN/MANUAL/DASHBOARD_MANUAL_HOLD, DIE_TO_WIRE_STAGING/MANUAL/DASHBOARD_MANUAL_HOLD, WIRE_TO_GATE/MANUAL/DASHBOARD_MANUAL_HOLD, STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
