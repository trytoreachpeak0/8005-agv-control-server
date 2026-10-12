# RIoT SDK 0.2.0-fp.5

Immutable local feed for ControlServer，完整产品线（v2，`fp/v2-impl`）。

- Source repository: `https://github.com/trytoreachpeak0/riot-sdk`
- Source branch: `fp/v2-facade`
- Exact source commit: `4d3b4381160a61f9e01cab00143cebe7a5d3714c`（riot-sdk#3 的合并提交）
- Package version: `0.2.0-fp.5`
- 上一版：`0.2.0-fp.4`（`fp/v2-facade` 的 `073e40f`）
- 没有打 git tag，也没有发 GitHub release。

打包命令（从该提交的 detached worktree 直接打，打包前后 `git status` 都干净）：

```powershell
dotnet pack .\csharp\RIoT.Sdk.sln -c Release `
  -p:Version=0.2.0-fp.5 `
  -p:RepositoryUrl=https://github.com/trytoreachpeak0/riot-sdk `
  -p:RepositoryType=git `
  -p:RepositoryCommit=4d3b4381160a61f9e01cab00143cebe7a5d3714c `
  -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg `
  -o <this directory>
```

三个 `.nuspec` 打完后逐个核对过：id、version、repository 的 url 与 commit，以及 Facade
对 Core／Generated 的依赖版本（`0.2.0-fp.5`），全部与本记录一致。文件摘要在 `SHA256SUMS`；
消费侧的完整性闸门是 NuGet lock 文件的 `contentHash`。

打包前在同一提交上的 SDK 验证：

- C#：147 passed, 0 failed, 0 skipped（`dotnet test csharp/RIoT.Sdk.Tests -c Release`）。
- Python：129 passed, 1 skipped（`pytest`，在 `python/` 下；跳过的是需要 `RIOT_SMOKE=1` 连真 RIoT 的冒烟用例）。
- riot-sdk CI（只跑 C#）：PR 最终头 `e22346b` 的 run `38208172754` 绿。合并提交与 `e22346b` 的文件内容
  逐一相同（`git diff e22346b 4d3b438` 为空），所以这一轮同样覆盖它。

## 这一版有什么

来自 8005-agv-control-server#639（车刚开机 `movementState=MT_NA` 被判非停稳）。对 `0.2.0-fp.4`
是纯增量，只改了 `GetVehicleExecutionFactsAsync` 一个方法。

### `VehicleExecutionFacts.Mode`

`VehicleExecutionFacts` 末尾加 `string? Mode`：`getVehicleInfo` 响应里 `vehicle.mode` 是 JSON 字符串、
并且恰好是 `MODE_AUTO`、`MODE_MANUAL`、`UNKNOWN` 之一（大小写敏感、不带空格）时原样带出；缺字段、
`null`、不是字符串、或任何别的拼写都是 null。消费端应当要求 `MODE_AUTO`，而不是排除 `MODE_MANUAL`。

不用生成层的枚举解析读它：Kiota 1.17.4 的 `EnumHelpers.GetEnumValue` 最后一步是忽略大小写、接受数字的
`Enum.TryParse`，会把 `"0"`、`"mode_auto"`、`" MODE_AUTO"` 读成 `MODE_AUTO`。

做法是只换解析、不换请求：请求照生成层 `GetAsync` 的原样发（同一个 `SendAsync`、不带错误映射），解析换成
Facade 内部的 `StrictModeVehicleAllInfo`／`StrictModeVehicle`，只有 `mode` 取原始字符串，其余字段都是生成层的
解析。超时、响应释放、错误映射、Content-Type 处理都与生成层一致（riot-sdk#3 第二轮审查 M1、S1 的结论）。

### 调用方要知道的一处行为变化

`mode` 不是 JSON 字符串时，`0.2.0-fp.4` 的生成层会抛异常（ControlServer 记 `RIOT_READ_FAILED`）；
本版读成 null（ControlServer 记 `RIOT_MODE_NOT_AUTO`）。两者都让车保持未知。

Do not replace a package while retaining this version. Publish a new immutable
version and regenerate the ControlServer lock files instead.
