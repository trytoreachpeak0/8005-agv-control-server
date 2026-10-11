# L2 场景证据：real-onboard-inflight-load-reconnect

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261011T043057313Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `b5043c81b58e2a399a54a5480178e67e31c8ded9` |
| onboardHmiCommit | `17668d22bdc53461ad23b071fa8215cdacbaa881` |
| protocolFaultProxy | `True` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-38111328241-1\_stage\l2-20261011T043057313Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端的会话经协议故障代理建立（否则断开注入不到这条链路上） | PASS | `>= 1 SessionHello through the proxy` | `1` |
| 重连后服务端在等这次装货的结果：新代次、RecoveryRequired、PENDING_FACT_RECONCILIATION_REQUIRED，待对账 attempt 含本次装货（CV-CONNECTION-LOSS-SAFE-FINISH 的 NEVER_READY_BEFORE_RECONCILIATION） | PASS | `gen > 1 / RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED / pending 含 66ae298f-f32c-7658-9bd2-2410338addb1` | `断 1 条 / gen 2 / RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED / pendingAttempts ["66ae298f-f32c-7658-9bd2-2410338addb1"] / pendingResults [] / unsettled 66ae298f-f32c-7658-9bd2-2410338addb1 / checkpoint ACTIVE_UNLOCK_SET / activeUnlock [1]` |
| 装货在车上结束后，这次装货的 OperationResult 在第一次重连后的这一代次内送达服务端并被受理，会话回到 Ready、装货 Committed（ADR-cross-0028／0029：未就绪期间允许结果补报；不靠第二次断线） | PASS | `CLOSED/OCCUPIED/1/0 / gen 2 内结果 >= 1 / gen 2 Ready / Committed` | `CLOSED/OCCUPIED/1/0 / gen 2 内结果 1（共 1） / gen 2 / Ready / READY / pendingAttempts [] / pendingResults [] / unsettled  / checkpoint ACTIVE_UNLOCK_SET / activeUnlock [1] / Committed` |
| （守护）RecoveryRequired 期间，这次装货的核对／收尾阶段 OperationProgress 仍不发（ADR 只放行结果补报，进度是遥测） | PASS | `0 VERIFYING/SAFE_FINISH progress after the drop` | `0 ()` |
| （探针）第二次断线重连后，结果在服务端、会话回 Ready、装货 Committed、旅程离开装货段 | PASS | `结果 >= 1 / gen > 2 Ready / Committed / AwaitingStationDeparture 或 AwaitingDepartureSafety 或 AwaitingGateArrival` | `断 1 条 / 结果 1 / gen 3 / Ready / READY / pendingAttempts [] / pendingResults [] / unsettled  / checkpoint RESULT_RECORDED / activeUnlock [] / Committed / AwaitingStationDeparture` |
| 同一 attempt 至多一条 OperationResult（服务端收件箱按 messageId 一行），不重复结算 | PASS | `<= 1` | `1 (66ae298f-f32c-7658-9bd2-2410338addb1 gen 2 outcome COMPLETED -> DurableAck,SessionReadiness)` |
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
