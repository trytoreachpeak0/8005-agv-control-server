# cs#202 绿证据

- 提交：`1e404804`（实现）
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter FullyQualifiedName~RecoveryStateMachineG2Tests`
- 结果：`Passed: 100, Failed: 0`，原文见 `console.txt` 与 `green.trx`

另在本机跑过连接状态相关的其它测试类（`OnboardMessageProcessorTests`、`JourneyRuntimeWorker*`、
`OnboardJourneyPublisherTests`、`ExpectedActionOverdueTests`、`OnboardTcpServerTests` 等，加上所有名字含
`Recovery` 的类）：366 条全绿。全量测试走 CI。
