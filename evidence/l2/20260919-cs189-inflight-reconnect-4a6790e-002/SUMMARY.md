# L2 场景证据：real-onboard-inflight-load-reconnect

结论：**FAIL**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T102201692Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `169dfa428fa1f8c9bb81a33fb60777c776776f52` |
| onboardHmiCommit | `4a6790e5753b8a350049494569f8ec4a82fdf1f1` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T102201692Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则断开注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 重连后服务端在等这次装货的结果：新代次、RecoveryRequired、PENDING_FACT_RECONCILIATION_REQUIRED，待对账 attempt 含本次装货（CV-CONNECTION-LOSS-SAFE-FINISH 的 NEVER_READY_BEFORE_RECONCILIATION） | PASS | `gen > 1 / RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED / pending 含 1d1c97b5-03da-2850-bf83-b9c29e1f643b` | `断 1 条 / gen 2 / RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED / pendingAttempts ["1d1c97b5-03da-2850-bf83-b9c29e1f643b"] / pendingResults [] / unsettled 1d1c97b5-03da-2850-bf83-b9c29e1f643b / checkpoint ACTIVE_UNLOCK_SET / activeUnlock [1]` |
| 装货在车上结束后，这次装货的 OperationResult 送达服务端并被受理，会话回到 Ready、装货 Committed（ADR-cross-0028／0029：未就绪期间允许结果补报） | FAIL | `CLOSED/OCCUPIED/1/0 / 结果 >= 1 / Ready / Committed` | `CLOSED/OCCUPIED/1/0 / 结果 1 / gen 2 / RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED / pendingAttempts ["1d1c97b5-03da-2850-bf83-b9c29e1f643b"] / pendingResults [] / unsettled 1d1c97b5-03da-2850-bf83-b9c29e1f643b / checkpoint ACTIVE_UNLOCK_SET / activeUnlock [1] / Prepared` |
| （观察）断线重连后这次装货的 OperationProgress 仍送达服务端 | FAIL | `>= 1 progress after the drop` | `0 ()` |
| （探针）第二次断线重连后，结果经车载端的中断结算送达，会话回 Ready、装货 Committed、旅程走到关卡段 | FAIL | `结果 >= 1 / Ready / Committed / AwaitingGateArrival` | `断 1 条 / 结果 1 / gen 3 / Ready / READY / pendingAttempts [] / pendingResults [] / unsettled  / checkpoint SAFE_FINISH_REACHED / activeUnlock [] / Committed / AwaitingLoadResult` |
| 同一 attempt 至多一条 OperationResult（服务端收件箱按 messageId 一行），不重复结算 | PASS | `<= 1` | `1 (1d1c97b5-03da-2850-bf83-b9c29e1f643b gen 3 outcome COMPLETED -> DurableAck,SessionReadiness)` |
| （观察，hmi#124 线索）全程没有 ProtocolProblem；重连后的连接上服务端发过的行程快照条数照实记录 | PASS | `0 ProtocolProblem` | `0 ProtocolProblem () / 重连后行程快照 0 条 ()` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
