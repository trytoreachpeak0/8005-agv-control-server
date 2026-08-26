# W2G-IS-00～07 服务端切片

| Slice | 权威输入 | 最小持久边界与接口 | 失败/恢复 | 验收 | 禁止副作用 |
| --- | --- | --- | --- | --- | --- |
| 00 | Session/Capability/Safety/Recovery schemas | session generation、inbox/outbox、快照 revision；TLS peer | 身份错配拒绝；恢复中再断线从已提交步骤继续 | 首连/重连进入 READY 或具名 RECOVERY_REQUIRED | TCP 在线即 READY；接受旧代次 |
| 01 | MesIngest V2、AcceptedDemandSnapshot、RIoT intent；`CV-DEMAND-ACCEPT-TO-PICKUP` | 最终重读；Demand 与 TransportDemandKey 防重；Accepted snapshot + TO_PICKUP intent 同事务 | 建单未知保留原 upperId 并查询 | 唯一受理、唯一订单、可信取货到站 | 写 MesIngest；超时换号建单 |
| 02 | worklist/Sublot/SlotOperation schemas | SublotReservation、Guard、完整目标仓位与命令原子建立；LoadBatch 整批提交 | 错码/容量不足在开门前拒绝；纠错/取消保留原绑定 | 多仓 OCCUPIED 闭环且只提交一次 | 部分装货成功；服务端覆盖 IO |
| 03 | PreDepartureSafetyCheck、RIoT TO_GATE | 新鲜检查与 TO_GATE intent；稳定 MovementLegId/upperId | 过期/UNSAFE/UNKNOWN 不移动；结果未知对账 | 仅安全窗口内一次移动并可信到关卡 | 复用历史结果；盲重试写调用 |
| 04 | OperationResult、TransportDemandCompletion | UnloadBatch + StopClosureCommit + Demand success + completion 同事务 | 重复结果重放同一结论 | 全部 EMPTY 后一次本地完成 | 等 PDA/MES；写 MES；重复完成 |
| 05 | connection-loss vectors | 断联事实、移动 hold 意图和恢复授权持久化 | 不扩大 ActiveUnlockSet；唯一继续或待恢复 | PREPARED/部分动作/结果边界均收敛 | 重连自动继续；在线超时取消 |
| 06 | delivery class、dedup keys、request replay | inbox/outbox、首个响应和内容哈希唯一约束 | 相同内容重放；异内容稳定冲突；UNKNOWN 对账 | drop/delay/duplicate 后恰好一个副作用 | 内存去重；生成新 MessageId 重试 |
| 07 | recovery/exception vectors | 每个恢复决定与 ForcedRecoveryGeneration 持久化 | crash 后使用原身份；歧义保持恢复 | 无重复订单/命令/完成 | 从头重放；迟到结果覆盖强制恢复 |

共同完成条件均为候选 G1、本端真实逻辑+Fake 的 G2、以及绑定精确两端构建的 G3；任何一项不能替代人员批准或现场资格。
