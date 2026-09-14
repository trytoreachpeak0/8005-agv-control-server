# L2 场景证据：g3-predeparture-check-expires

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T041957709Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `052759bca58a04316dfda260249b3b5697f2cf8e` |
| onboardHmiCommit | `b96010825d43aeee3b861cb3b4716f4d0873c8a0` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| protocolReleaseIdentity.schemaBundleSha256 | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T041957709Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：检查 → 它的答复 → 安全状态变化（版本越过检查问的版本）→ 车载端回 ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED) 拒收那张检查（CV-PREDEPARTURE-SAFETY-EXPIRES orderedExpectedMessages / stableErrorCode） | PASS | `Check(v) < Result < SafetyStateChanged(>v) < ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED, rejected=Check)` | `Check=v6 / Result=SAFE / SafetyStateChanged=v8 / ProtocolProblem×1 / 有序=True` |
| 不凭过期的检查出发：去关卡的单晚于重问那张检查的 SAFE 答复，旅程消费的答复就是重问那张的，不是过期那张（NEVER_DEPART_ON_EXPIRED_CHECK） | PASS | `AwaitingGateArrival / 建单晚于重问的 SAFE / 消费的是重问的答复` | `AwaitingGateArrival / 建单晚于重问=True / consumed=af98cf08-fd33-0a53-8c16-16ad60101639` |
| 安全状态变化使检查作废：过期那张的发件箱行已作废，重问那张是新身份，问的是变化之后的安全版本（EXPIRE_CHECK_ON_SAFETY_STATE_CHANGE） | PASS | `过期那张作废 / 新身份 / 问 v≥8` | `作废=True / 新身份=True / 问 v8` |
| 车载端及时报告安全状态变化：锁反馈被改后 5 秒内报不安全，放开后 5 秒内报安全（REPORT_SAFETY_STATE_CHANGE_PROMPTLY） | PASS | `≤5s / ≤5s` | `0.09s / 0.12s` |
| 过期之后重问并凭新答复出发：重问那张得到车载端的 SAFE，去关卡的意图恰好一条、RIoT 上的关卡单恰好一张（REREQUEST_CHECK_AFTER_EXPIRY） | PASS | `SAFE / 意图 1 / 关卡单 1` | `SAFE / 意图 1 / 关卡单 1` |
| 拒收过期检查不断会话：会话代次在整个过程中不变，结束时仍 Ready | PASS | `代次 1 / Ready` | `代次 1 / Ready` |
| 终态没有重复提交也没有未证实的物理状态：一笔装载 Committed，8 号仓锁反馈已恢复，装载仓关着、有货、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE） | PASS | `1 笔 Committed / 8 号锁反馈 1 / 装载仓 CLOSED` | `1 笔 Committed / 8 号锁反馈 1 / 装载仓 CLOSED` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
