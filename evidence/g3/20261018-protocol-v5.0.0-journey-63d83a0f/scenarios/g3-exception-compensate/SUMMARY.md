# L2 场景证据：g3-exception-compensate

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261018T074256926Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261018T074256926Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 消息顺序与向量一致，各一次：SessionRequested → Opened → ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted → LoadCompensationRequested → LoadCompensationCommand → LoadCompensationResult（CV-EXCEPTION-COMPENSATE orderedExpectedMessages） | PASS | `各 1，按向量顺序` | `SessionRequested×1 / Action×1 COMPENSATE_LOAD_ALL_EMPTY / CompensationRequested×1 / Command×1 / Result×1 / 有序=True` |
| 补偿依托恢复会话授权：工作流挂在这次打开的恢复会话上，命令就是工作流绑定的那一条，指向同一会话、同一尝试、同一仓位（AUTHORIZE_COMPENSATION_AGAINST_RECOVERY_SESSION） | PASS | `会话 f78fd4af-68d4-b85d-aa59-f40b2541e0d8 / 命令 = 工作流绑定 / attempt bea41d30-876d-8d55-b63b-ac41fbe13eb9 / 仓 1` | `会话 f78fd4af-68d4-b85d-aa59-f40b2541e0d8 / 命令 92a272e5-0a67-e351-a85f-155e71e17603 vs 绑定 92a272e5-0a67-e351-a85f-155e71e17603 / attempt bea41d30-876d-8d55-b63b-ac41fbe13eb9 / 仓 1` |
| 补偿只执行一次、证空不开门：一条命令、一份结果；按下补偿之后没有任何开锁（EXECUTE_COMPENSATION_ONCE / forbidden duplicate-slot-unlock） | PASS | `命令 1 / 结果 1 / 补偿后开锁 0` | `命令 1 / 结果 1 / 补偿后开锁 0` |
| 车载端报补偿后的仓位状态：ALL_EMPTY，每仓空、锁上、输出复位，与模拟器一致（REPORT_COMPENSATED_SLOT_STATE） | PASS | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0` | `ALL_EMPTY 1=COMPLETED/EMPTY/LOCKED/RESET / 1=CLOSED/EMPTY/1/0` |
| 补偿收敛且无重复提交：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾，没有去关卡，RIoT 上只有取货那一张单（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE） | PASS | `Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / TO_GATE 0 / RIoT 单 1` | `Reconciled / CLOSED / Cancelled / Cancelled / 释放=True / Completed/CANCELLED_BY_LOAD_COMPENSATION / TO_GATE 0 / RIoT 单 1` |
| 补偿收敛之后车辆放出来了：这条需求所在旅程的用途占有记录已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival、没有停摆原因码；control-server#131，#387 起读用途占有） | PASS | `用途占有记录已释放 / 下一单 AwaitingPickupArrival on AGV-L2-001，TO_PICKUP 意图 CONFIRMED，没有停摆原因码` | `ClaimRecord.ReleasedAt='2026-10-18 07:43:43.2096594+00:00' / 下一单 AwaitingPickupArrival/ TO_PICKUP=CONFIRMED on AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
