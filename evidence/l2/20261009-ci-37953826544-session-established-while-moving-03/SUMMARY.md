# L2 场景证据：session-established-while-moving

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T163057495Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T163057495Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 会话建立时车载端报告车辆仍在运动 | PASS | `vehicleStopped=False @ safetyStateVersion=1` | `vehicleStopped=False @ safetyStateVersion=1` |
| 车在动不影响会话就绪（就绪只看 departureSafe） | PASS | `Ready` | `Ready` |
| 车在动时需求判 ONBOARD_DEPARTURE_UNSAFE | PASS | `ONBOARD_DEPARTURE_UNSAFE` | `ONBOARD_DEPARTURE_UNSAFE` |
| 被拒的需求没有 journey，也没有建单 | PASS | `(no runtime, no intent)` | `runtime=(none) intent=(none)` |
| 车停稳后同一条需求被受理并派车 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 同一条 backlog 记录翻成 ACCEPTED | PASS | `ACCEPTED` | `ACCEPTED` |
| 推进是被车载端发出的 SafetyStateChanged 推动的 | PASS | `>= 1` | `1` |
| 车载端仍报运动时，服务端不采信 RIoT 的到站 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 车载端报停稳后到站被采信，进入 AwaitingSublot | PASS | `AwaitingSublot` | `AwaitingSublot` |
| 全程只建了一条 RIoT 单（被拒期间没有派过车） | PASS | `1` | `1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
