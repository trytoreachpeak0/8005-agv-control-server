# cs#151 staged G3：新判据（绿证据）

- 结论：`STAGED_G3_RECOVERY_REPLAY_PASS`；`FP-IS-00`、`FP-IS-06`、`FP-IS-07`、`FP-IS-14`、`FP-IS-15` 全部 `PASS`。
- 绑定：cs#90 的 `06b65688`——服务端 `e0f26b37`、车载端 `9748c418`、模拟器 `fb5f7c59`、协议 `86575456`。
- harness：`166acd80` = `06b65688` + cs#151 判据提交（PR 分支上是 `fa66d59b`），在临时本地克隆里运行，启动时干净。
- 关键观测：第 2 代强制工作流 `Reconciled`，恢复会话 `CLOSED`（代次 2），第 1 代 `HistoricalOnly`；唯一的硬件恢复记录指向第 2 代工作流；没有建单、需求、站点作业；恢复车辆就绪 `RecoveryRequired`（原因 `HANDSHAKE_INCOMPLETE`，探针车辆不完成握手，见 runner 判据注释的覆盖边界）。
- 对照：同一绑定、判据退回旧版本的一次在 `../20260919-b5-151-staged-old-criteria-06b65688/`。
