# cs#200 e 红证据：解除暂停的 ReleasedBy 是新 GUID

- 提交：`455e14e2`（只加测试）
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter FullyQualifiedName~TaskTypeStationActivationFollowUpTests.AHoldRelease`
- 结果：`Failed: 1`，两条暂停的 `ReleasedBy` 是 `fieldops:release-hold:<另一个 GUID>`，不等于 `fieldops:release-hold:` + 返回的审计记录号。
