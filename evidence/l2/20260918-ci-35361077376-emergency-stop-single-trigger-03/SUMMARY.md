# L2 场景证据：emergency-stop-single-trigger

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T152150520Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35361077376-1\_stage\l2-20260918T152150520Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车在正常行驶、没有任何故障时，一次急停都没发 | PASS | `0 行 / 0 次` | `0 行 / 0 次` |
| 该发时发了：单被报 FAILED 且车证不出停住，服务端发出 triggerEmergency，记在这台车名下 | PASS | `AGV-L2-001 / vehicle:BROKERX-L2-0001` | `AGV-L2-001 / vehicle:BROKERX-L2-0001` |
| 线上收到的是 triggerEmergency，打在这台车的 deviceKey 上 | PASS | `triggerEmergency @ BROKERX-L2-0001` | `1 次，首次打在 BROKERX-L2-0001` |
| 回读时闩锁还没锁上，这一行记为 Pending，没有被当成已停住 | PASS | `Pending` | `Pending` |
| 只发一次：闩锁还没锁上、车仍在动，又评估了 4 轮，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次 | PASS | `1 行 / 1 次` | `1 行 / 1 次` |
| 只发一次：闩锁读不到（不是 OK，也不是锁上），又评估了 4 轮，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次 | PASS | `1 行 / 1 次` | `1 行 / 1 次` |
| 闩锁锁上之后，发出它的那一行被改记为 Confirmed，不再永远停在 Pending | PASS | `Confirmed` | `Confirmed` |
| 只发一次：闩锁已确认，又评估了 4 轮，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次 | PASS | `1 行 / 1 次` | `1 行 / 1 次` |
| 故障事实停在 SuspectedBlocked，证据码 VEHICLE_ORDER_FAILED，并记下了升级时刻 | PASS | `SuspectedBlocked / VEHICLE_ORDER_FAILED / 有升级时刻` | `SuspectedBlocked / VEHICLE_ORDER_FAILED / 2026-09-18 15:22:16.6558228+00:00` |
| OrderHold 也只发了一次：单已经是 FAILED，重发改变不了什么 | PASS | `1` | `1` |
| 只发一次：车停下来不再升级，又评估了 5 轮，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次 | PASS | `1 行 / 1 次` | `1 行 / 1 次` |
| 恢复严：故障原因（单 FAILED）还在，服务端一次 cancelEmergency 都没发 | PASS | `0 行 / 0 次` | `0 行 / 0 次` |
| 原因消除前闩锁意外恢复 OK：服务端重触发，第二行的原因写的是 EMERGENCY_LATCH_RELEASED_EXTERNALLY（REQ-0248） | PASS | `AttemptNumber 2 / EMERGENCY_LATCH_RELEASED_EXTERNALLY` | `AttemptNumber 2 / {"source":"Automatic","requesterIdentity":null,"agvId":"AGV-L2-001","deviceKey":"BROKERX-L2-0001","Reason":"EMERGENCY_LATCH_RELEASED_EXTERNALLY","call":{"disposition":"Accepted","Operation":"triggerEmergency","Classification":"SdkAccepted","HttpStatusCode":null,"BusinessCode":null,"FailureCategory":null,"observedAt":"2026-09-18T15:22:26.6669213\u002B00:00"},"emergencyState":"OK"}` |
| 全程两次 triggerEmergency：一次升级、一次外部解除后的重触发；两行都已确认，RIoT 侧计数一致，没有 cancelEmergency | PASS | `2 行（均 Confirmed） / 2 次 / 0 次 cancelEmergency` | `2 行（Confirmed 2） / 2 次 / 0 次 cancelEmergency` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
