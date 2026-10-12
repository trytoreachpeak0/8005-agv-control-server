# RIoT SDK 0.2.0-fp.6

Immutable local feed for ControlServer，完整产品线（v2，`fp/v2-impl`）。

- Source repository: `https://github.com/trytoreachpeak0/riot-sdk`
- Source branch: `fp/v2-facade`
- Exact source commit: `cf58c11fe3e698a57f29d77687d59aa807a27d6a`（riot-sdk#5 的合并提交，修 riot-sdk#4）
- Package version: `0.2.0-fp.6`
- 上一版：`0.2.0-fp.5`（`fp/v2-facade` 的 `4d3b438`）
- 没有打 git tag，也没有发 GitHub release。

打包命令（从该提交的干净克隆直接打，打包前后 `git status` 都干净）：

```powershell
dotnet pack .\csharp\RIoT.Sdk.sln -c Release `
  -p:Version=0.2.0-fp.6 `
  -p:RepositoryUrl=https://github.com/trytoreachpeak0/riot-sdk `
  -p:RepositoryType=git `
  -p:RepositoryCommit=cf58c11fe3e698a57f29d77687d59aa807a27d6a `
  -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg `
  -o <this directory>
```

三个 `.nuspec` 打完后逐个核对过：id、version、repository 的 url 与 commit，以及 Facade
对 Core／Generated 的依赖版本（`0.2.0-fp.6`），全部与本记录一致。文件摘要在 `SHA256SUMS`；
消费侧的完整性闸门是 NuGet lock 文件的 `contentHash`（五个 lock 文件里三个包的 `contentHash`
与本目录 `.nupkg` 的 SHA-512 逐一相同）。

SDK 侧的验证记录在 riot-sdk#5 正文：C# 246 passed，Python 153 passed、1 skipped（需要
`RIOT_SMOKE=1` 的冒烟用例），以及逐项变异。本次打包没有重跑 SDK 测试。

## 这一版有什么

来自 riot-sdk#4／#5。对 `0.2.0-fp.5` 只改了 `RIoT.Sdk.Facade` 的 `RiotWire.cs` 一个源文件
（`git diff 4d3b438 cf58c11 -- csharp` 只有它和一个新测试文件），影响的只有经 `RiotWire.ReadAsync`
的 6 个读取：`mapInfo/edges/{mapId}`、`mapInfo/stations/{mapId}`、`mapResource/removedEdge/{mapId}`、
`mapResource/removedStation/{mapId}`、`mapEdgeGroup/all`、`task/v1/route/`。Core 与 Generated 源码未变，
只是随版本号重打。

- 请求改走生成层同一个 `SendAsync`，`HttpClient.Timeout` 因此覆盖响应体：响应头之后响应体停住会按
  超时抛 `TaskCanceledException`，改前会一直挂住。
- 失败响应（404、500 等）的 content 会被释放，改前不释放。

### 调用方要知道的行为变化

1. Content-Type 与生成层一致：缺 Content-Type 报 `RiotApiException`；`text/html`、`text/plain`、
   `text/json` 等非 `application/json` 抛 `InvalidOperationException`。改前不看 Content-Type。
2. 数字经 Kiota 无类型模型转一道：整数字面量原样；`128.0`、`1.28E2` 写在 int 字段上照旧失败关闭；
   超出 float 范围的数按 `RiotApiException` 失败关闭（改前能按 double 读出）。
3. 持续慢速涓流的响应会比改前更早超时。

Do not replace a package while retaining this version. Publish a new immutable
version and regenerate the ControlServer lock files instead.
