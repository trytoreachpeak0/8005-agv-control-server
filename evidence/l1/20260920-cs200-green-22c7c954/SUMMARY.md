# cs#200 绿证据

- 提交：`22c7c954`（a～e 全部实现）
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter "FullyQualifiedName~TaskTypeStationActivation|FullyQualifiedName~TaskTypeBindingDashboardTests|FullyQualifiedName~DashboardSkeletonTests|FullyQualifiedName~Governance|FullyQualifiedName~TaskTypeHold|FullyQualifiedName~TaskTypeStationStartup|FullyQualifiedName~AgvRestoration|FullyQualifiedName~SlotConfiguration"`
- 结果：`Passed: 200, Failed: 0`，原文见 `console.txt` 与 `green.trx`。含本票新增的 12 条、改写的 1 条看板测试，以及 d 的共享修法所涉调用方的测试类。全量测试走 CI。
