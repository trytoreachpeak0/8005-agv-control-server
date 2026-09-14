# L2 场景证据：g3-manual-charging-return

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T043245733Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T043245733Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：每次申请 ManualChargingReturnToServiceRequested 都得到一份关联到它的 ManualChargingReturnToServiceResult，两次申请请求号不同（CV-MANUAL-CHARGING-RETURN orderedExpectedMessages） | PASS | `申请 2 / 关联应答 2 / 请求号不同` | `申请 2 / 关联应答 2 / 请求号 b09a24a5-9b23-45c1-8351-4d0be459c54e, e1a0a5ec-a875-44ee-aa1c-f6332b9221e6` |
| 申请带着已验证的管理员：两次请求都带车上配置的管理员号与核验方式，服务端记下同一管理员与 MAINTENANCE_ADMINISTRATOR 角色（REQUIRE_VERIFIED_ADMINISTRATOR / REQUEST_RETURN_WITH_OPERATOR_CONTEXT） | PASS | `2 行 / 管理员号一致 / MAINTENANCE_ADMINISTRATOR` | `2 行 / L2-OPERATOR(MAINTENANCE_ADMINISTRATOR), L2-OPERATOR(MAINTENANCE_ADMINISTRATOR)` |
| 服务端据会话当时的状态重新评估：会话 RecoveryRequired 时拒绝（带原因），会话 Ready 时受理为 RETURNED_TO_ELIGIBILITY_EVALUATION（REEVALUATE_ELIGIBILITY_AFTER_RETURN） | PASS | `RecoveryRequired → REJECTED(原因) / Ready → RETURNED_TO_ELIGIBILITY_EVALUATION` | `RecoveryRequired → REJECTED(SESSION_RECOVERY_REQUIRED) / Ready → RETURNED_TO_ELIGIBILITY_EVALUATION` |
| 申请本身没有副作用：会话仍 Ready，没有需求、仓位操作、开锁进度或 RIoT 单，八个仓都关着、空、锁上（forbidden duplicate-riot-order、duplicate-slot-unlock、ready-before-reconciliation / NO_UNPROVEN_STATE） | PASS | `Ready / 需求 0 / 操作 0 / 进度 0 / RIoT 单 0 / CLOSED/EMPTY/1/0` | `Ready / 需求 0 / 操作 0 / 进度 0 / RIoT 单 0 / CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
