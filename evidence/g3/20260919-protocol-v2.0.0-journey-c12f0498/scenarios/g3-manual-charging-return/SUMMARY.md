# L2 场景证据：g3-manual-charging-return

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T175558872Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `c12f0498281a39e4fa94a51bd4506c3d3af97a1c` |
| onboardHmiCommit | `29fbf65e0b4d58c80849d5e6d0e44f40903c411e` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260918T175558872Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致：每次申请 ManualChargingReturnToServiceRequested 都得到一份关联到它的 ManualChargingReturnToServiceResult，两次申请请求号不同（CV-MANUAL-CHARGING-RETURN orderedExpectedMessages） | PASS | `申请 2 / 关联应答 2 / 请求号不同` | `申请 2 / 关联应答 2 / 请求号 0bf925ab-fae3-4cbf-97e0-77ad94a777cb, b60245ea-f1fd-45a3-81ad-37f2e5d14e1a` |
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
