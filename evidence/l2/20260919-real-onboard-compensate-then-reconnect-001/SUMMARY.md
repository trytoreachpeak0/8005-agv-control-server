# L2 场景证据：real-onboard-compensate-then-reconnect

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T161723461Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
| onboardHmiCommit | `9748c4187e74aeb46cf95557e8f2430e8fe2abf3` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260918T161723461Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则断开注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 前半段到位：重启后中断结算把空着关上的装货报 UNKNOWN，服务端判 RecoveryRequired，旅程停摆 | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked` |
| 未到达：车载端没有给出「补偿清空」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「补偿清空」入口` |
| 未到达：车载端没有给出「补偿清空」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「补偿清空」入口` |
| 未到达：车载端没有给出「补偿清空」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「补偿清空」入口` |
| 未到达：车载端没有给出「补偿清空」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「补偿清空」入口` |
| 未到达：车载端没有给出「补偿清空」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「补偿清空」入口` |
| 未到达：车载端没有给出「补偿清空」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「补偿清空」入口` |
| 未到达：车载端没有给出「补偿清空」入口 | FAIL | `(reached)` | `(not reached) 车载端没有给出「补偿清空」入口` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
