# L2 场景证据：real-onboard-expected-action-overdue

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T090022042Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `8e638a44c89698e2b1f00971934d341320570b25` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `8f308bb1b839bf214f488e406c5cc3ac1b74de4e` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T090022042Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 空关后车自己重开：这次装货的 UNLOCKING 进度 ≥ 2 条，1 号仓门又弹开、空着、开锁输出复位，重开早于门槛 | PASS | `UNLOCKING ≥ 2 / OPEN/EMPTY/0/0 / 重开早于门槛` | `UNLOCKING 2 / OPEN/EMPTY/0/0 / 重开于第一次开锁后 8.7 s` |
| 门槛之前没有超时告警：最新一份告警快照里没有这个码，端点 slots 为空，HMI 上 ExpectedActionOverdue 不在 UIA 树里，服务端没要过中途快照；读完仍在门槛之前 | PASS | `告警 0 / 端点 0 行 / HMI 无 / 请求 0 / 读于门槛前` | `告警 0 / 端点 0 行 / HMI 无 / 请求 0 / 读于门槛前 9.7 s` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |
| 未到达：越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE | FAIL | `(reached)` | `(not reached) 越过门槛 50 秒内车载端没有报 SLOT_EXPECTED_ACTION_OVERDUE` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
