# cs#202 红证据：握手中发出恢复命令与会话快照

- 提交：`83e42c11`（只有测试，产品代码与 `fp/v2-impl@e74c0058` 相同）
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter <本票新测试与 ACompensationAuthorizedAfterItsSessionClosedIsRefusedAndDoesNotReopenIt>`
- 结果：`Failed: 5, Passed: 2`，原文见 `console.txt` 与 `red.trx`

五条红的都红在同一处：握手里补发的拒绝／结果／恢复请求，答复后面紧跟了
`ExceptionRecoverySessionSnapshot`（请求那条还有 `SlotOperationResumeCommand`）。立即发送模式下快照甚至排在
`DurableAck` 之前。两条绿的是守护测试（握手之后照旧立即发送）和改写后的审查 C 测试（活路径在现有代码上本来就被拦住）。

实现提交里握手断言改为以恢复报告那一轮为界；改后的测试已在基线实现上复核仍是 5 红 1 绿（见 PR 正文）。
