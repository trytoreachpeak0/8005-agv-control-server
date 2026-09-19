# L2 场景证据：real-onboard-unload-not-emptied

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T144736059Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `3274feae1275508be2a1f14b5ff1f66f59beb031` |
| onboardHmiCommit | `4d716340982de4e39339c2151c291efe1a21e1d1` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35449602428-1\_stage\l2-20260919T144736059Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：装货正常提交，仓位关门、锁上、有货，旅程进到去关卡那一段 | PASS | `Committed / CLOSED/OCCUPIED/1/0` | `Committed / CLOSED/OCCUPIED/1/0` |
| 卸的是装货用的那个仓，车载端说在等操作员时门确实开着、货还在、开锁输出已复位 | PASS | `slot 1 / OPEN/OCCUPIED/0/0` | `slot 1 / OPEN/OCCUPIED/0/0` |
| 第 1 轮带货关门：车载端自己重新开锁（UNLOCKING +1）、门又弹开（模拟器 OPEN、货还在、开锁输出复位）、再次等操作员 | PASS | `UNLOCKING 1→2 / WAITING_OPERATOR 增加 / OPEN/OCCUPIED/0/0` | `UNLOCKING 1→2 / WAITING_OPERATOR 1→2 / OPEN/OCCUPIED/0/0` |
| 第 2 轮带货关门：车载端自己重新开锁（UNLOCKING +1）、门又弹开（模拟器 OPEN、货还在、开锁输出复位）、再次等操作员 | PASS | `UNLOCKING 2→3 / WAITING_OPERATOR 增加 / OPEN/OCCUPIED/0/0` | `UNLOCKING 2→3 / WAITING_OPERATOR 2→3 / OPEN/OCCUPIED/0/0` |
| 重开 2 轮之后什么都没结算：卸货仍在途（Prepared）、车载端一份结果都没报、旅程等在 AwaitingUnloadResult、需求 Accepted、没有恢复 | PASS | `Prepared / 0 result / AwaitingUnloadResult / Accepted / 会话 Ready / 恢复会话 0 / 恢复工作流 0` | `Prepared / 0 result / AwaitingUnloadResult / Accepted / 会话 Ready / 恢复会话 0 / 恢复工作流 0` |
| 取空后车载端报 COMPLETED（整个卸货 attempt 唯一一份结果），卸货 Committed，仓位空、门关、锁上 | PASS | `COMPLETED / Committed cd489842-d0a0-2c5c-8521-8993f1edcbb0 / CLOSED/EMPTY/1/0` | `COMPLETED / Committed cd489842-d0a0-2c5c-8521-8993f1edcbb0 / CLOSED/EMPTY/1/0` |
| 需求完成（Succeeded），旅程 Completed | PASS | `Succeeded / Completed` | `Succeeded / Completed` |
| 全程没有进过恢复 | PASS | `会话 Ready / 恢复会话 0 / 恢复工作流 0` | `会话 Ready / 恢复会话 0 / 恢复工作流 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
