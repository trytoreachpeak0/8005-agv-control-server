# cs#200 d 红证据（审查补充）：外层事务里数据库真拒插入

- 测试提交：`34d459c4`；运行时把 `GovernanceStore.cs` 临时换回 `fp/v2-impl` 的版本（差异见 `injected-fault.diff`，未提交），跑完即还原。
- 测试：`AnAuditTheDatabaseRejectsInsideTheCallersTransactionIsNotCommittedWithTheRestOfIt`。用触发器 `RAISE(ABORT)` 让数据库真的拒绝插入，而且发生在调用方的外层事务里，EF 回滚到保存点。
- 结果：`Failed: 1`。报错 `Microsoft.EntityFrameworkCore.DbUpdateException` ← `SQLite Error 19: 'cs200 refused'`，出在第二次写入上：被拒的那行留在跟踪器里，被同一上下文的下一次保存又交了一遍，数据库再次拒绝，把调用方后面的写入一起拖垮。
