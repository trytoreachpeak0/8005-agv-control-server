# RIoT SDK 0.2.0-fp.2

Immutable local feed for ControlServer，完整产品线（线 B）。

- Source repository: `https://github.com/trytoreachpeak0/riot-sdk`
- Source branch: `fp/v2-facade`
- Exact source commit: `e9b7411055ceeea8cf902688a8e3b424a4ddb70a`
- Package version: `0.2.0-fp.2`

打包命令（从 worktree 直接打，仓库已提交且干净）：

```powershell
dotnet pack .\csharp\RIoT.Sdk.sln -c Release `
  -p:Version=0.2.0-fp.2 `
  -p:RepositoryUrl=https://github.com/trytoreachpeak0/riot-sdk `
  -p:RepositoryType=git `
  -p:RepositoryCommit=e9b7411055ceeea8cf902688a8e3b424a4ddb70a `
  -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg `
  -o <immutable-feed>
```

三个 `.nuspec` 打完后逐个核对：id、version、repository 的 url 与 commit、以及 Facade 对
Core／Generated 的依赖版本，全部与本记录一致。文件摘要在 `SHA256SUMS`；消费侧的完整性
闸门是 NuGet lock 文件的 `contentHash`。

打包前的 SDK 验证：

- C#: 92 passed, 0 failed, 0 skipped（`dotnet test csharp/RIoT.Sdk.Tests`）。
- Python: 88 passed, 1 skipped（环境依赖的 smoke），`pytest tests`。

## 这一版有什么

### 一、五个 imap 路网只读端点的具名 Facade

`CP-0001` 修订项二把它们批进了 `REQ-0146` 的具名清单，`RouteGraphSnapshot` 引擎要靠它们取
设计态与运行态：

| 方法 | 端点 |
| --- | --- |
| `ListEdgesAsync` | `GET /api/imap/v1/mapInfo/edges/{mapId}` |
| `ListStationDetailsAsync` | `GET /api/imap/v1/mapInfo/stations/{mapId}` |
| `ListRemovedEdgesAsync` | `GET /api/imap/v1/mapResource/removedEdge/{mapId}` |
| `ListRemovedStationsAsync` | `GET /api/imap/v1/mapResource/removedStation/{mapId}` |
| `ListEdgeGroupsAsync` | `GET /api/imap/v1/mapEdgeGroup/all` |

`removedEdgeDetail` **不在其中**，`CP-0001` 明确不批。配套领域类型在 `RIoT.Sdk.Core`：
`MapEdge`、`MapStationDetail`、`RemovedEdge`、`RemovedStation`、`MapEdgeGroup`。它们的
反序列化由 SDK 内部完成（`ADR-sdk-0009`），线格式的四处怪癖不外溢到 ControlServer。

### 二、把 `codex/riot-sdk-controlserver-integration` 的 12 个 API 合回了主线

**这条是升级本包前必须知道的事。**`0.1.0-controlserver.2` 是从旧仓库 `8005---AGV` 的分支
`codex/riot-sdk-controlserver-integration`（commit `e708f874`）打的，而**那个分支的代码从未
合回 SDK 主线**——`riot-sdk` 建仓以来的全历史里搜不到 `OrderSnapshot`。后果是 riot-sdk 的
源码此前打不出 ControlServer 正在消费的那个包。本次为路网端点重新打包时才暴露：编译直接
失败在 `OrderSnapshot` 找不到。

合回的内容（ControlServer 12 个 API 全部在用）：

- `RIoT.Sdk.Core`：`OrderSnapshots.cs`（`OrderSnapshot`／`OrderMissionSnapshot`／
  `OrderLookupStatus`／`OrderLookupResult`）、`OrderStatePage.cs`、`VehicleFacts.cs`
- `RIoT.Sdk.Facade`：`FindOrderByUpperIdAsync`、`ListOrdersByStatesAsync`、
  `GetVehicleCardAsync`、`GetVehicleExecutionFactsAsync`、`ListStationsStrictAsync`
- 另带 `RiotSession` 的自有 no-retry `HttpClient` 与 `SendRawAsync`、`RiotAuthClient` 的
  no-redirect 传输与取消／超时区分

因此**本包的 API 面是 `0.1.0-controlserver.2` 的超集**，升级是纯增量：既有方法一字未动。

## 版本号为什么是 `.2` 而不是 `.1`

`0.2.0-fp.1` 打过两次，内容不同（第一次在主线补齐之前），第一次已经进过本机的 NuGet 全局
包缓存。同一个版本号承载过两份内容，这个号就脏了——即使它从未提交、从未离开这台机器。按本
目录一贯的 immutable 约定作废该号，改用 `.2`。

**这条规则不是形式主义**：第二次打包后构建仍然报 `OrderSnapshot` 找不到，正是因为缓存里躺
着第一份 `fp.1`，restore 不会重新解压同名同版本的包。

Do not replace a package while retaining this version. Publish a new immutable
version and regenerate the ControlServer lock files instead.
