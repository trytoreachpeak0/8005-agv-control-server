# cs#200 b 红证据：看板把墓碑原样显示成码值

- 提交：`b6cc9665`（只改测试断言）
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter FullyQualifiedName~TaskTypeBindingDashboardTests.AMapLeftWithoutAnActiveBindingSetByAManualClose`
- 结果：`Failed: 1`，`Assert.Contains() Failure: Sub-string not found`：HTML 是 `生效指针 CLOSED_MANUALLY`，找不到 `生效指针：无生效版本，已收尾，等 FieldOps 换上新的一版`。
