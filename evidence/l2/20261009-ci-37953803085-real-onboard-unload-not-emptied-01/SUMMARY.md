# L2 场景证据：real-onboard-unload-not-emptied

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T155357033Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
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
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37953803085-1\_stage\l2-20261009T155357033Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：装货正常提交，仓位关门、锁上、有货，旅程进到去关卡那一段 | PASS | `Committed / CLOSED/OCCUPIED/1/0` | `Committed / CLOSED/OCCUPIED/1/0` |
| 卸的是装货用的那个仓，车载端说在等操作员时门确实开着、货还在、开锁输出已复位 | PASS | `slot 1 / OPEN/OCCUPIED/0/0` | `slot 1 / OPEN/OCCUPIED/0/0` |
| 第 1 轮带货关门：车载端自己重新开锁（UNLOCKING +1）、门又弹开（模拟器 OPEN、货还在、开锁输出复位）、再次等操作员 | PASS | `UNLOCKING 1→2 / WAITING_OPERATOR 增加 / OPEN/OCCUPIED/0/0` | `UNLOCKING 1→2 / WAITING_OPERATOR 1→2 / OPEN/OCCUPIED/0/0` |
| 第 2 轮带货关门：车载端自己重新开锁（UNLOCKING +1）、门又弹开（模拟器 OPEN、货还在、开锁输出复位）、再次等操作员 | PASS | `UNLOCKING 2→3 / WAITING_OPERATOR 增加 / OPEN/OCCUPIED/0/0` | `UNLOCKING 2→3 / WAITING_OPERATOR 2→3 / OPEN/OCCUPIED/0/0` |
| 重开 2 轮之后什么都没结算：卸货仍在途（Prepared）、车载端一份结果都没报、旅程等在 AwaitingUnloadResult、需求 Accepted、没有恢复 | PASS | `Prepared / 0 result / AwaitingUnloadResult / Accepted / 会话 Ready / 恢复会话 0 / 恢复工作流 0` | `Prepared / 0 result / AwaitingUnloadResult / Accepted / 会话 Ready / 恢复会话 0 / 恢复工作流 0` |
| 取空后车载端报 COMPLETED（整个卸货 attempt 唯一一份结果），卸货 Committed，仓位空、门关、锁上 | PASS | `COMPLETED / Committed 4bb39d19-0292-a955-9bb8-6b8424841e6f / CLOSED/EMPTY/1/0` | `COMPLETED / Committed 4bb39d19-0292-a955-9bb8-6b8424841e6f / CLOSED/EMPTY/1/0` |
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
