# L2 场景证据：real-onboard-restart-after-recovery-session-opened

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260920T051022430Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-7` |
| controlServerCommit | `1157e522a3abe0a2cffb423c03427f4840a92782` |
| onboardHmiCommit | `dc1f033b60adc081c0676226a433e075a96fc4cc` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-35490938927-1\_stage\l2-20260920T051022430Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked | PASS | `UNKNOWN / RecoveryRequired / Blocked` | `UNKNOWN / RecoveryRequired / Blocked/LOAD_RESULT_REQUIRES_RECOVERY` |
| 重启前会话开成、动作已受理、受理应答被丢：SessionRequested → Opened 送达，ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted 而这条应答被代理按 messageId 丢掉（车放弃按下之后才判）；这辆车只有这一个会话行，ACTION_SELECTED、选中补偿、revision 2；工作流恰一个 AwaitingAuthorization 且还没发出补偿命令 | PASS | `会话行 1：ACTION_SELECTED、COMPENSATE_LOAD_ALL_EMPTY、r2 / 工作流 1：AwaitingAuthorization、无命令 / 补偿命令 0` | `会话行 1：ACTION_SELECTED、动作='COMPENSATE_LOAD_ALL_EMPTY'、r2 / 工作流 1：COMPENSATE_LOAD_ALL_EMPTY、AwaitingAuthorization、命令='' / 补偿命令 0` |
| 断电时车载端自己已经记着这次会话：日志库 WireToGateRecoveryState 的 exceptionRecoverySessionId 等于服务端开出的会话 id，动作向量与动作 id 也已落盘。这是本场景唯一读车载端库的判据——要证的那一格（车记着会话、内存里没有快照）就在它的持久状态里 | PASS | `会话 '5f9fb4b9-8e4a-805c-9a77-1418048da82a'、动作 id 非空、向量 LOAD_COMPENSATION` | `会话 '5f9fb4b9-8e4a-805c-9a77-1418048da82a'、动作 '9c2eea80-4039-5252-abb8-b5774e0ba601'、向量 LOAD_COMPENSATION/9c2eea80-4039-5252-abb8-b5774e0ba601` |
| 重启前有一份可重放的快照：这个会话在发件箱里最后一份既未确认也未 fence，ACTION_SELECTED、r2、选中 COMPENSATE_LOAD_ALL_EMPTY、绑在当前会话代次；更早的 OPEN r1 已被它 fence | PASS | `待重放恰 1 份：r2 ACTION_SELECTED/COMPENSATE_LOAD_ALL_EMPTY g2；r1 已 fence` | `2 份：r1 OPEN/- g2 ack=False fenced=True → r2 ACTION_SELECTED/COMPENSATE_LOAD_ALL_EMPTY g2 ack=False fenced=False` |
| 服务端在重启后的恢复状态报告之后把那份快照重放进新会话：同一 messageId 的发件箱行改绑到新代次，载荷不变（仍 ACTION_SELECTED r2），仍未确认、未 fence，没有另起快照（ReplayPendingCommandsAsync） | PASS | `RecoveryStateReport g>2 在重启之后 / 同一行 g=报告代次、r2 ACTION_SELECTED、载荷相同、ack=False fenced=False / 快照仍 2 份` | `RecoveryStateReport g3 重启后=True / 同一行 g3 r2 ACTION_SELECTED 载荷相同=True ack=False fenced=False / 快照 2 份` |
| 重启后的恢复入口可用：第一次按「补偿清空」就走到补偿结果（没有「补偿清空失败」，即车载端没有在本地抛 RECOVERY_SESSION_STATE_PENDING）；重启后没有再申请会话；这辆车始终只有一个恢复会话行，还是重启前那一个 | FAIL | `第一次按下即有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1，仍是 5f9fb4b9-8e4a-805c-9a77-1418048da82a` | `第一次按下：REFUSED；再按一次：REFUSED / 重启后 SessionRequested 0 / 会话行 1：5f9fb4b9-8e4a-805c-9a77-1418048da82a` |
| 未到达：重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED） | FAIL | `(reached)` | `(not reached) 重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED）` |
| 未到达：重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED） | FAIL | `(reached)` | `(not reached) 重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED）` |
| 未到达：重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED） | FAIL | `(reached)` | `(not reached) 重启后「补偿清空」没有走到 LoadCompensationResult（REFUSED；再按一次：REFUSED）` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
