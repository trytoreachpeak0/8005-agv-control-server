# L2 场景证据：emergency-stop-single-trigger

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T160153851Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T160153851Z-slot3` |
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
| 故障事实停在 SuspectedBlocked，证据码 VEHICLE_ORDER_FAILED，并记下了升级时刻 | PASS | `SuspectedBlocked / VEHICLE_ORDER_FAILED / 有升级时刻` | `SuspectedBlocked / VEHICLE_ORDER_FAILED / 2026-10-09 16:02:28.2500506+00:00` |
| OrderHold 也只发了一次：单已经是 FAILED，重发改变不了什么 | PASS | `1` | `1` |
| 只发一次：车停下来不再升级，又评估了 5 轮，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次 | PASS | `1 行 / 1 次` | `1 行 / 1 次` |
| 恢复严：故障原因（单 FAILED）还在，服务端一次 cancelEmergency 都没发 | PASS | `0 行 / 0 次` | `0 行 / 0 次` |
| 原因消除前闩锁意外恢复 OK：服务端重触发，第二行的原因写的是 EMERGENCY_LATCH_RELEASED_EXTERNALLY（REQ-0248） | PASS | `AttemptNumber 2 / EMERGENCY_LATCH_RELEASED_EXTERNALLY` | `AttemptNumber 2 / {"source":"Automatic","requesterIdentity":null,"agvId":"AGV-L2-001","deviceKey":"BROKERX-L2-0001","Reason":"EMERGENCY_LATCH_RELEASED_EXTERNALLY","call":{"disposition":"Accepted","Operation":"triggerEmergency","Classification":"SdkAccepted","HttpStatusCode":null,"BusinessCode":null,"FailureCategory":null,"observedAt":"2026-10-09T16:02:38.224954\u002B00:00"},"emergencyState":"OK"}` |
| 全程两次 triggerEmergency：一次升级、一次外部解除后的重触发；两行都已确认，RIoT 侧计数一致，没有 cancelEmergency | PASS | `2 行（均 Confirmed） / 2 次 / 0 次 cancelEmergency` | `2 行（Confirmed 2） / 2 次 / 0 次 cancelEmergency` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
