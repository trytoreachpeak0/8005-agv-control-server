# L2 场景证据：g3-predeparture-check-expires

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261018T073924753Z` |
| agvId | `AGV-L2-001` |
| batchId | `v5-exit` |
| controlServerCommit | `63d83a0f623a99d0260c2b1c95672a91584336b5` |
| onboardHmiCommit | `a3a6753f6924b8f845483d6b886259c90af9b018` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261018T073924753Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：检查 → 它的答复 → 安全状态变化（版本越过检查问的版本）→ 车载端回 ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED) 拒收那张检查（CV-PREDEPARTURE-SAFETY-EXPIRES orderedExpectedMessages / stableErrorCode） | PASS | `Check(v) < Result < SafetyStateChanged(>v) < ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED, rejected=Check)` | `Check=v6 / Result=SAFE / SafetyStateChanged=v8 / ProtocolProblem×1 / 有序=True` |
| 不凭过期的检查出发：去关卡的单晚于重问那张检查的 SAFE 答复，旅程消费的答复就是重问那张的，不是过期那张（NEVER_DEPART_ON_EXPIRED_CHECK） | PASS | `AwaitingGateArrival / 建单晚于重问的 SAFE / 消费的是重问的答复` | `AwaitingGateArrival / 建单晚于重问=True / consumed=e50ed2f7-deda-2c5c-a760-20ab103b4202` |
| 安全状态变化使检查作废：过期那张的发件箱行已作废，重问那张是新身份，问的是变化之后的安全版本（EXPIRE_CHECK_ON_SAFETY_STATE_CHANGE） | PASS | `过期那张作废 / 新身份 / 问 v≥8` | `作废=True / 新身份=True / 问 v8` |
| 车载端及时报告安全状态变化：锁反馈被改后 5 秒内报不安全，放开后 5 秒内报安全（REPORT_SAFETY_STATE_CHANGE_PROMPTLY） | PASS | `≤5s / ≤5s` | `0.2s / 0.06s` |
| 过期之后重问并凭新答复出发：重问那张得到车载端的 SAFE，去关卡的意图恰好一条、RIoT 上的关卡单恰好一张（REREQUEST_CHECK_AFTER_EXPIRY） | PASS | `SAFE / 意图 1 / 关卡单 1` | `SAFE / 意图 1 / 关卡单 1` |
| 拒收过期检查不断会话：会话代次在整个过程中不变；拒收之后车凭安全状态出发；结束时 Ready，或者未就绪完全由这次出发解释（DEPARTURE_SAFETY_NOT_READY、只含车辆运动原因、那版安全状态晚于建单、关卡单未结束，control-server#138） | PASS | `代次 1 / Ready 或出发解释的降级` | `代次 1 / Ready` |
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
