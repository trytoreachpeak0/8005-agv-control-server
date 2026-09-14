# L2 场景证据：g3-exception-compensate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T042529968Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T042529968Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：SessionRequested → Opened → ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted → LoadCompensationRequested → LoadCompensationCommand → LoadCompensationResult（CV-EXCEPTION-COMPENSATE orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `SessionRequested×1 / Action×1 COMPENSATE_LOAD_ALL_EMPTY / CompensationRequested×1 / Command×1 / Result×1 / 有序=True` |
| 补偿依托恢复会话授权：工作流挂在这次打开的恢复会话上，命令就是工作流绑定的那一条，指向同一会话、同一尝试、同一仓位（AUTHORIZE_COMPENSATION_AGAINST_RECOVERY_SESSION） | PASS | `会话 6f6a6eac-8c61-6c58-a589-c0b04e7fdf86 / 命令 = 工作流绑定 / attempt 2db2f3fb-ff70-cf59-bb9f-8d23dc18f5ca / 仓 1` | `会话 6f6a6eac-8c61-6c58-a589-c0b04e7fdf86 / 命令 d1cceef1-173a-8e5a-821d-332dec270296 vs 绑定 d1cceef1-173a-8e5a-821d-332dec270296 / attempt 2db2f3fb-ff70-cf59-bb9f-8d23dc18f5ca / 仓 1` |
| 补偿只执行一次、证空不开门：一条命令、一份结果；按下补偿之后没有任何开锁（EXECUTE_COMPENSATION_ONCE / forbidden duplicate-slot-unlock） | PASS | `命令 1 / 结果 1 / 补偿后开锁 0` | `命令 1 / 结果 1 / 补偿后开锁 0` |
| 车载端报补偿后的仓位状态：ALL_EMPTY，每仓空、锁上、输出复位，与模拟器一致（REPORT_COMPENSATED_SLOT_STATE） | PASS | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0` | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0` |
| 补偿收敛且无重复提交：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾，没有去关卡，RIoT 上只有取货那一张单（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE） | PASS | `Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / TO_GATE 0 / RIoT 单 1` | `Reconciled / CLOSED / Cancelled / Cancelled / 释放=True / Completed/CANCELLED_BY_LOAD_COMPENSATION / TO_GATE 0 / RIoT 单 1` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
