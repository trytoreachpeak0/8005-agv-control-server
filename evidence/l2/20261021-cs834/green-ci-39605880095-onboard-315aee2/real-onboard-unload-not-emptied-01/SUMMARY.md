# L2 场景证据：real-onboard-unload-not-emptied

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261021T131426404Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `9086018a5fb062887fd66d47ac602250b7b357f0` |
| onboardHmiCommit | `315aee25083ca6f956e672e10be8b07201aed78a` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\vmwin01-control-server-desktop\_work\_temp\real-rig-39605880095-1\_stage\l2-20261021T131426404Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：装货正常提交，仓位关门、锁上、有货，旅程进到去关卡那一段 | PASS | `Committed / CLOSED/OCCUPIED/1/0` | `Committed / CLOSED/OCCUPIED/1/0` |
| 卸的是装货用的那个仓，车载端说在等操作员时门确实开着、货还在、开锁输出已复位 | PASS | `slot 1 / OPEN/OCCUPIED/0/0` | `slot 1 / OPEN/OCCUPIED/0/0` |
| 第 1 轮带货关门：车载端不自己重开（关门后 5 秒门关着、锁扣上、没有 UNLOCKING）；按「重新开门」后开锁一次（UNLOCKING +1）、门又弹开（模拟器 OPEN、货还在、开锁输出复位）、再次等操作员 | PASS | `关门后车没有自己重开 / 按了重新开门、重开 / UNLOCKING 1→2 / WAITING_OPERATOR 增加 / OPEN/OCCUPIED/0/0` | `空关后车没有自己重开（UNLOCKING 1→1；+0s CLOSED/OCCUPIED/0/0 -> +0.2s CLOSED/OCCUPIED/1/0） / 按了重新开门、重开 / UNLOCKING 1→2 / WAITING_OPERATOR 1→3 / OPEN/OCCUPIED/0/0` |
| 第 2 轮带货关门：车载端不自己重开（关门后 5 秒门关着、锁扣上、没有 UNLOCKING）；按「重新开门」后开锁一次（UNLOCKING +1）、门又弹开（模拟器 OPEN、货还在、开锁输出复位）、再次等操作员 | PASS | `关门后车没有自己重开 / 按了重新开门、重开 / UNLOCKING 2→3 / WAITING_OPERATOR 增加 / OPEN/OCCUPIED/0/0` | `空关后车没有自己重开（UNLOCKING 2→2；+0s CLOSED/OCCUPIED/0/0 -> +0.2s CLOSED/OCCUPIED/1/0） / 按了重新开门、重开 / UNLOCKING 2→3 / WAITING_OPERATOR 3→5 / OPEN/OCCUPIED/0/0` |
| 重开 2 轮之后什么都没结算：卸货仍在途（Prepared）、车载端一份结果都没报、旅程等在 AwaitingUnloadResult、需求 Accepted、没有恢复 | PASS | `Prepared / 0 result / AwaitingUnloadResult / Accepted / 会话 Ready / 恢复会话 0 / 恢复工作流 0` | `Prepared / 0 result / AwaitingUnloadResult / Accepted / 会话 Ready / 恢复会话 0 / 恢复工作流 0` |
| 取空后车载端报 COMPLETED（整个卸货 attempt 唯一一份结果），卸货 Committed，仓位空、门关、锁上 | PASS | `COMPLETED / Committed 7f204255-6ff6-1255-9c6f-8efe2c99de8c / CLOSED/EMPTY/1/0` | `COMPLETED / Committed 7f204255-6ff6-1255-9c6f-8efe2c99de8c / CLOSED/EMPTY/1/0` |
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
