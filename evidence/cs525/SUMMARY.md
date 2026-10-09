# control-server#525 证据：非终态订单逐页读全

基线 `fp/v2-impl@a11c7aa4`。命令都是：

```
dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --filter "<见下>"
```

## 修前红（`red-before-fix.txt`）

用例提交 `db0d6cf4`，网关源文件临时换回基线版本。过滤 `FullyQualifiedName~HttpRiotMovementGatewayTests`：

```
Failed!  - Failed:     6, Passed:    85, Skipped:     0, Total:    91
```

红的 6 个正是「多页应读全」的两组用例 × 三处读法（安全读数、按车读、全清单）。
`APagedReadThatDoesNotAddUpIsIncomplete` 的 18 个在旧代码上本来就绿：旧代码只要超过一页就一律判读不全，
它们是防止修复放宽过头的护栏，不是修前红的证据。

## 修后绿（`green-after-fix.txt`）

过滤 `HttpRiotMovementGatewayTests|RiotCallAllowlistArchitectureTests|FakeRiotTests|ArchitectureTests`，退出码 0：

```
Passed!  - Failed:     0, Passed:   183, Skipped:     0, Total:   183
```

## 变异（手工，跑完即还原）

| 变异 | 结果 |
| --- | --- |
| 去掉「每页总数须与第 1 页一致」 | 6 红（`total-grows`／`total-shrinks` × 三处） |
| 去掉「同一记录 id 不得重复」 | 3 红（`record-repeated` × 三处） |

## 不在本证据里

本机全量与 CI 等调度放行后再跑。
