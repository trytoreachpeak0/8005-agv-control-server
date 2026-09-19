# cs#200 a 红证据：对账审计还没有 pointerAfter

- 提交：`352764d6`（只加测试；产品代码与 `905ffd1d` 相同，这几处文件在 `905ffd1d..3dc7a431` 之间没有改动）
- 命令：`dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter FullyQualifiedName~TaskTypeStationActivationFollowUpTests`
- 结果：`Failed: 4`，四条都报 `System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.`——审计详情里没有 `pointerAfter` 这个字段。四条在读审计之前的指针断言都已通过。
