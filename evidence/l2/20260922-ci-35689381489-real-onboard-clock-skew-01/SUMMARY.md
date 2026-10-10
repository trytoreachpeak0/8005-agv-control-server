# L2 场景证据：real-onboard-clock-skew

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T052525168Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| clockSkewMs | `100` |
| controlServerCommit | `f1286c29aa0cbeffdd750f3016a52590dd4659b9` |
| onboardHmiCommit | `ecdb3a0be1d95ef51e7d40493808ba4659274f41` |
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
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35689381489-1\_stage\l2-20260922T052525168Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的安全投影确实经过了偏差代理，且 observedAt 被推后 | PASS | `forwardedRequests >= 1，位移 = 100 ms` | `forwardedRequests = 7，位移 = 100 ms` |
| 车载端时钟慢 100 ms（容差内）时会话正常建立 | PASS | `Ready/…` | `Ready/READY` |
| 偏差 3000 ms 超出容差时车载端仍然 fail-closed，会话降级 | PASS | `RecoveryRequired/DEPARTURE_SAFETY_NOT_READY` | `RecoveryRequired/DEPARTURE_SAFETY_NOT_READY` |
| 需求随之被拒，判 ONBOARD_FACTS_NOT_READY | PASS | `ONBOARD_FACTS_NOT_READY` | `ONBOARD_FACTS_NOT_READY` |
| 运行时又转了几轮，仍然没有为这条需求建 journey | PASS | `(没有 journey)` | `(没有 journey)` |
| 偏差回到容差内后，同一条需求被受理——车载端自行恢复，没有重启 | PASS | `ACCEPTED` | `ACCEPTED` |
| 恢复之后旅程真的建起来并派车 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 会话从 RecoveryRequired 自己回到 Ready，全程没有重启车载端 | PASS | `Ready/…` | `Ready/READY` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
