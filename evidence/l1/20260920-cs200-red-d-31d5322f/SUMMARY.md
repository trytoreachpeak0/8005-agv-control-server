# cs#200 d 红证据：被拒的审计行被下一次保存补交

- 提交：`31d5322f`（只加测试；`GovernanceStore.cs` 与 `905ffd1d` 相同）
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter ` 三条 d 测试
- 结果：`Failed: 3`。
  - 业务审计、管理员审计各一条：`Expected: "…|CS200_NEXT"`，`Actual: "…|CS200_REFUSED"`——插入被拒的那条审计被同一上下文的下一次保存写进了库。
  - `ARefusedAuditLeavesTheCallersOwnPendingChangesTracked`：`Collection: [BusinessAuditRecordRow {…} Added]`——失败后被拒的行仍以 `Added` 留在跟踪器里。
