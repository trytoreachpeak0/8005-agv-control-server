# L2 场景证据：g3-fault-cargo-handoff

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260914T042754887Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260914T042754887Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：RecoveryActionSubmitted(FAULT_CARGO_HANDOFF) → RecoveryActionAccepted → FaultCargoRecoveryCommand → FaultCargoRecoveryResult（CV-FAULT-CARGO-HANDOFF orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `Action×1 RecoveryActionAccepted FAULT_CARGO_HANDOFF / Command×1 / Result×1 / 有序=True` |
| 服务端记下这次交接：工作流带交接号与结果 HANDED_OFF，状态 Reconciled（RECORD_FAULT_CARGO_HANDOFF） | PASS | `交接号已记 / HANDED_OFF / Reconciled` | `交接号 bd64a891-26f6-bc50-a518-643b9213bb42 / HANDED_OFF / Reconciled` |
| 只凭授权的命令交接：命令就是工作流绑定的那一条，命令、结果、工作流三处交接号相同；按下交接之后没有额外开锁（HANDOFF_ONLY_ON_AUTHORIZED_COMMAND / forbidden duplicate-slot-unlock） | PASS | `命令 = 绑定 / 交接号 bd64a891-26f6-bc50-a518-643b9213bb42 ×3 / 开锁 0` | `命令 36dd4470-faf4-d55e-8985-fba7485504a2 / 命令交接号 bd64a891-26f6-bc50-a518-643b9213bb42 / 结果交接号 bd64a891-26f6-bc50-a518-643b9213bb42 / 工作流 bd64a891-26f6-bc50-a518-643b9213bb42 / 开锁 0` |
| 车载端报交接结果：HANDED_OFF，每个授权仓空、锁上、输出复位（REPORT_HANDOFF_OUTCOME） | PASS | `HANDED_OFF 1=COMPLETED/EMPTY/LOCKED/RESET` | `HANDED_OFF 1=COMPLETED/EMPTY/LOCKED/RESET` |
| 交接收敛且无重复提交：需求与装载 Cancelled，旅程以 TERMINATED_BY_FAULT_CARGO_HANDOFF 收尾、没有去关卡，RIoT 上只有取货那一张单，仓位物理上空、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE） | PASS | `Cancelled / Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / TO_GATE 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0` | `Cancelled / Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / TO_GATE 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
