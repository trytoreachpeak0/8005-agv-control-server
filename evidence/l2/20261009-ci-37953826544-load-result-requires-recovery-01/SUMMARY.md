# L2 场景证据：load-result-requires-recovery

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160556264Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160556264Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 装载指令已下发，旅程在等结果 | PASS | `AwaitingLoadResult` | `AwaitingLoadResult` |
| 旅程进入 Blocked | PASS | `Blocked` | `Blocked` |
| 阻塞原因是 LOAD_RESULT_REQUIRES_RECOVERY | PASS | `LOAD_RESULT_REQUIRES_RECOVERY` | `LOAD_RESULT_REQUIRES_RECOVERY` |
| 装载操作判 RecoveryRequired（不是 Committed，也不是停在 Prepared） | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 需求状态转为 RecoveryRequired | PASS | `RecoveryRequired` | `RecoveryRequired` |
| 装载没成时不建 TO_GATE 单（RIoT 单仍只有一条） | PASS | `1` | `1` |
| 装载转入恢复后，它造成的不安全不再被自身命令豁免 | PASS | `DEPARTURE_SAFETY_NOT_READY` | `DEPARTURE_SAFETY_NOT_READY` |
| 会话离开 Ready 后阻塞原因不被 ONBOARD_SESSION_NOT_READY 覆盖 | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| 车载端关机后阻塞原因仍在 | PASS | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` | `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| Blocked 期间新需求连候选评估都没进（没有 backlog 记录） | PASS | `0` | `0` |
| 新需求没有被受理 | PASS | `0` | `0` |
| 新需求没有 journey | PASS | `(none)` | `(none)` |
| Blocked 期间没有为任何需求派过车（RIoT 单仍只有一条） | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
