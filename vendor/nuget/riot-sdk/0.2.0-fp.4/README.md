# RIoT SDK 0.2.0-fp.4

Immutable local feed for ControlServer，完整产品线（v2，`fp/v2-impl`）。

- Source repository: `https://github.com/trytoreachpeak0/riot-sdk`
- Source branch: `fp/v2-facade`
- Exact source commit: `073e40fd0f01c91302990df2684c0bc12a7e40f4`（riot-sdk#2 的合并提交）
- Package version: `0.2.0-fp.4`
- 上一版：`0.2.0-fp.3`（`fp/v2-facade` 的 `d9462a6`；那个目录没有 README）

打包命令（从该提交的 detached worktree 直接打，`git status` 干净）：

```powershell
dotnet pack .\csharp\RIoT.Sdk.sln -c Release `
  -p:Version=0.2.0-fp.4 `
  -p:RepositoryUrl=https://github.com/trytoreachpeak0/riot-sdk `
  -p:RepositoryType=git `
  -p:RepositoryCommit=073e40fd0f01c91302990df2684c0bc12a7e40f4 `
  -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg `
  -o <this directory>
```

三个 `.nuspec` 打完后逐个核对过：id、version、repository 的 url 与 commit，以及 Facade
对 Core／Generated 的依赖版本（`0.2.0-fp.4`），全部与本记录一致。文件摘要在 `SHA256SUMS`；
消费侧的完整性闸门是 NuGet lock 文件的 `contentHash`。

打包前在同一提交上的 SDK 验证：

- C#：110 passed, 0 failed, 0 skipped（`dotnet test csharp/RIoT.Sdk.Tests -c Release`）。
- Python：104 passed, 1 skipped（`pytest`，在 `python/` 下、`PYTHONPATH` 指向该检出；跳过的是
  需要 `RIOT_SMOKE=1` 连真 RIoT 的冒烟用例）。
- riot-sdk CI（只跑 C#）：run `36558958309` 绿。

## 这一版有什么

来自 8005-agv-control-server#401（批次9-03，RIoT 充电建单）。对 `0.2.0-fp.3` 是纯增量，既有
方法的行为一字未动。

### 一、`CreateMoveOrderAsync` 的「move + act」同名重载

充电订单是 `move(目标桩) + act(78, actionParam1 = 1, actionParam2 = 0)`（program 仓 RIoT 调用
白名单第 1.2 节「形态二」）。新重载多带一个 `RIoT.Sdk.Core.OrderMissionAction`，请求体为
`[{move, mapId, destination}, {act, actionId, actionParam1, actionParam2}]`，其余字段与单段
逐字相同。原签名的请求体**逐字节不变**（SDK 测试把改动前的请求体写成常量，并在改动前的提交上跑
过同一个常量）。

**是重载、不是新名字**：ControlServer 的 `RiotCallAllowlistArchitectureTests` 按 Facade 方法名
比对白名单，同名重载让它不改仍绿。

act 段**不带 `actionName`**，与 2026-09-12 现场在 agv01 上真正接上电的那张充电单一致（MVP 构建
`3b379bb8`，证据 `origin/ControlServer_MVP:evidence/field/20260912-FW-FL2-charging/`）。

一处调用方要知道的二义性：按位置给第五个参数传裸 `null`（`CreateMoveOrderAsync(u, k, m, d, null)`）
会报 `CS0121`，因为 `null` 两个重载都接得住。按名字传或省略不受影响。

### 二、`OrderMissionSnapshot` 读出 act 段

以可选 init 属性加了 `ActionId`、`ActionParam1`、`ActionParam2`、`ResultCode`，位置参数不变，
既有构造点不破。字段缺失读回 `null` 而不是 0；`resultStr` 这类自由文本不投影（REQ-0175）。
判「充不上」要的 act 段结果码（Round 24 证据为 407802）由此读得到。

### 三、基线

`fp/v2-facade` 在本版合入了 `origin/main`，`global.json` 由 `8.0.424` 抬到 `8.0.425`
（ADR-cross-0056）。

Do not replace a package while retaining this version. Publish a new immutable
version and regenerate the ControlServer lock files instead.
