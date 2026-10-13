# RIoT SDK 0.2.0-fp.7

Immutable local feed for ControlServer，完整产品线（v2，`fp/v2-impl`）。

- Source repository: `https://github.com/trytoreachpeak0/riot-sdk`
- Source branch: `fix/cs617-detail-by-order-id-without-upper-id`（PR 进 `fp/v2-facade`）
- Exact source commit: `62a827d3907a3f06365a3106c5422bffca52a0fe`（control-server#617）
- Package version: `0.2.0-fp.7`
- 上一版：`0.2.0-fp.6`（`fp/v2-facade` 的 `cf58c11`）
- 没有打 git tag，也没有发 GitHub release。

打包命令（从该提交的干净克隆直接打，打包前后 `git status` 都干净）：

```powershell
dotnet pack .\csharp\RIoT.Sdk.sln -c Release `
  -p:Version=0.2.0-fp.7 `
  -p:RepositoryUrl=https://github.com/trytoreachpeak0/riot-sdk `
  -p:RepositoryType=git `
  -p:RepositoryCommit=62a827d3907a3f06365a3106c5422bffca52a0fe `
  -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg `
  -o <this directory>
```

三个 `.nuspec` 打完后逐个核对过：id、version、repository 的 url 与 commit，以及 Facade
对 Core／Generated 的依赖版本（`0.2.0-fp.7`），全部与本记录一致。文件摘要在 `SHA256SUMS`；
消费侧的完整性闸门是 NuGet lock 文件的 `contentHash`。

SDK 侧的验证：C# 256 passed（原 246 + 新 10），Python 162 passed、1 skipped（需要
`RIOT_SMOKE=1` 的冒烟用例；原 153 + 新 9）。

## 这一版有什么

来自 control-server#617。对 `0.2.0-fp.6` 只改了门面 `OrderClient` 的一处校验（C# 与 Python 两边），
公开面不变：没有新方法，`OrderRef` 类型不变。

- `GetOrderByOrderIdAsync`（`GET detailByOrderId/{orderId}`）不再要求 `upperId`：RIoT 网页端手动派的单
  `upperId` 是空串，改前抛 `RiotApiException`（`order-ref-missing`），改后照常读出，`OrderRef.UpperId`
  为空串；`null` 或缺字段也读成空串。`id`、`orderId`、`orderState` 与 `result` 仍然必须有，缺了照旧
  `order-ref-missing`（真实 RIoT 查无此单是 HTTP 200 且无 `result`，仍落在这里）。
- 建单回执（`byDefaultMissions`）与 `GetOrderByUpperIdAsync` 不变，仍要求 `upperId`。

Do not replace a package while retaining this version. Publish a new immutable
version and regenerate the ControlServer lock files instead.
