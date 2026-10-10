# L2 场景证据：g3-fault-cargo-handoff

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T172829052Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-10` |
| controlServerCommit | `37a86cbc6bec866e32126f50c850c37a1a9cac40` |
| onboardHmiCommit | `535c94fce47a10879ba1f404d04f603ba1a65bbf` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T172829052Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：RecoveryActionSubmitted(FAULT_CARGO_HANDOFF) → RecoveryActionAccepted → FaultCargoRecoveryCommand → FaultCargoRecoveryResult（CV-FAULT-CARGO-HANDOFF orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `Action×1 RecoveryActionAccepted FAULT_CARGO_HANDOFF / Command×1 / Result×1 / 有序=True` |
| 服务端记下这次交接：工作流带交接号与结果 HANDED_OFF，状态 Reconciled（RECORD_FAULT_CARGO_HANDOFF） | PASS | `交接号已记 / HANDED_OFF / Reconciled` | `交接号 91943db8-b31f-6b5d-a0e6-399b219308ce / HANDED_OFF / Reconciled` |
| 只凭授权的命令交接：命令就是工作流绑定的那一条，命令、结果、工作流三处交接号相同；按下交接之后没有额外开锁（HANDOFF_ONLY_ON_AUTHORIZED_COMMAND / forbidden duplicate-slot-unlock） | PASS | `命令 = 绑定 / 交接号 91943db8-b31f-6b5d-a0e6-399b219308ce ×3 / 开锁 0` | `命令 6a6f035d-4591-1453-923f-43962adb5795 / 命令交接号 91943db8-b31f-6b5d-a0e6-399b219308ce / 结果交接号 91943db8-b31f-6b5d-a0e6-399b219308ce / 工作流 91943db8-b31f-6b5d-a0e6-399b219308ce / 开锁 0` |
| 车载端报交接结果：HANDED_OFF，每个授权仓空、锁上、输出复位（REPORT_HANDOFF_OUTCOME） | PASS | `HANDED_OFF 1=COMPLETED/EMPTY/LOCKED/RESET` | `HANDED_OFF 1=COMPLETED/EMPTY/LOCKED/RESET` |
| 交接收敛且无重复提交：需求与装载 Cancelled，旅程以 TERMINATED_BY_FAULT_CARGO_HANDOFF 收尾、没有去关卡，RIoT 上只有取货那一张单，仓位物理上空、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE） | PASS | `Cancelled / Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / TO_GATE 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0` | `Cancelled / Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / TO_GATE 0 / RIoT 单 1 / 1=CLOSED/EMPTY/1/0` |
| 交接收敛之后车辆放出来了：这条需求所在旅程的用途占有记录已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival、没有停摆原因码；control-server#131，#387 起读用途占有） | PASS | `用途占有记录已释放 / 下一单 AwaitingPickupArrival on AGV-L2-001，TO_PICKUP 意图 CONFIRMED，没有停摆原因码` | `ClaimRecord.ReleasedAt='2026-10-10 17:29:13.301252+00:00' / 下一单 AwaitingPickupArrival/ TO_PICKUP=CONFIRMED on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
