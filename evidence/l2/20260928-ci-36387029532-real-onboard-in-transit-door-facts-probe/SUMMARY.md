# L2 场景证据：real-onboard-in-transit-door-facts

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260928T063429382Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `716b40dfb5447a0d75825c4ea46a6aa3d0295798` |
| onboardHmiCommit | `4c2d2dc14656f80e812f37128a7964c2c310217a` |
| protocolFaultProxy | `True` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-36387029532-1\_stage\l2-20260928T063429382Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 取货段行驶中抽到了样本 | PASS | `>= 10` | `24` |
| 关卡段行驶中抽到了样本 | PASS | `>= 10` | `29` |
| 行驶中断线后，新一代会话的首条安全快照在 60 秒内到库 | PASS | `a SafetyStateSnapshot of a newer generation` | `SafetyStateSnapshot gen 2 after 2237 ms` |
| 行驶中锁反馈变为未锁，服务端收到 allTargetSlotsLocked=false | PASS | `observed` | `after 116 ms, reasons LOCK_NOT_CLOSED,ACTION_NOT_ALLOWED_IN_STATE` |
| 行驶中 IO 失联，服务端收到 SLOT_STATE_UNKNOWN | PASS | `observed` | `after 1058 ms` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
