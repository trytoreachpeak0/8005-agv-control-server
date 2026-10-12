# cs#531 证据：实例定义带上 MapStationCatalog 两个批准值

## 背景

REQ-0302 要求地图／站点目录的同步周期 `MapStationCatalog:ApprovedSyncPeriod` 与最长未确认时长
`MapStationCatalog:ApprovedMaxUnconfirmed` 先批准再投产；不配时服务端照常启动，但所有依赖地图的需求被拒，日志 2110
`CATALOG_PARAMETERS_NOT_APPROVED`（`CatalogAvailabilityAccess.cs:105`，判据 `MapStationCatalogOptions.IsApproved`）。
用户 2026-10-09 批准 `00:00:30` / `00:05:00`（workspace-v2 `ds-tasks/board.md` 10-09 条目）。实例定义白名单没有这一节，
10-09、10-10、10-11、10-12 每次重装后都在 factory01 上手改 `appsettings.Production.json`。

本票只跑脚本自检与定向单元测试，没有部署、没有碰 factory01 上运行中的 V2 实例、MVP、agv01 与生产 MesIngest。

## 自检结果（读到的）

| 运行 | 代码 | 结果 | 文件 |
| --- | --- | --- | --- |
| 基线 | `origin/fp/v2-impl` `0695c7627`，未改动 | 755 passed, 0 failed | `selftest-baseline.txt` |
| 修前 | `0695c7627` 的代码 + 本票的 `Test-ParallelInstance.ps1` | 759 passed, 34 failed | `selftest-prefix.txt` |
| 修后 | 本票 `4ab1b14ef` | 794 passed, 0 failed | `selftest-postfix.txt` |
| 静态检查 | `scripts/Test-ScriptStaticChecks.ps1`，本票 | 32/32 passed | — |

本票新增 39 条检查；修前那一轮跑的是其中 38 条（`Program.cs` 源码守卫是之后加的，修前跑必红，见变异 M15），4 条是绿的，都是修前就该绿的：「整节不写可以部署」「定义已批准、事件也绑定一致→放行」「整节不写、事件绑定为空→放行」
「事件里没有该字段（旧包）→放行」。这 4 条修前是绿的，是因为那时根本不比较，单独看不说明任何事；它们的作用是防止修后把这些情况误拒，
修后由变异 M9、M10、M11 证明比较确实在做。

## 定向单元测试（读到的）

`dotnet test tests/ControlServer.Tests --filter "FullyQualifiedName~ParallelInstanceEffectiveConfigurationTests|FullyQualifiedName~ArchitectureTests"`
（VSTest，SDK 8.0.425，经 `Invoke-HeavyLocal.ps1`）：**123 passed, 0 failed**（`dotnet-targeted.txt`）。只跑
`ParallelInstanceEffectiveConfigurationTests` 一个类时 44 条全过，其中本票新增 13 条（五份出厂定义绑定 5、覆盖现场手改 2、
事件字段 1、端到端回读 5）——13 是数源码里的 `[Fact]`／`[InlineData]` 得到的，没有逐条列名核对。

## 变异验证

每条变异在 `F:/8005-scratch/cs531-mut`（本票工作树的副本）上做：替换必须正好命中一处，改完的文件必须能解析（`.ps1`/`.psm1` 用
PowerShell 解析器、`.json` 能读、`.cs` 编译 Host 0 错误），然后跑一遍 `Test-ParallelInstance.ps1`（C 开头两条跑上面那个测试类），再还原。驱动脚本 `mutate.py`、`mutate_cs.py`，原始输出
`mutations-script.txt`、`mutations-csharp.txt`。「被抓」＝至少一条断言变红。

| 编号 | 改坏了什么 | 结果 | 抓住它的检查（举例） |
| --- | --- | --- | --- |
| M1 | 白名单去掉顶层 `mapStationCatalog` | 被抓（188 红） | 出厂五份被拒 |
| M2 | 去掉「两值同写同不写」 | 被抓（3 红） | 只写一个值、空节 |
| M3 | 格式改用 TimeSpan 自带解析（收 `"30"`、天数、小数秒） | 被抓（3 红） | `"30"`、`1.00:00:00`、`00:00:30.5` |
| M4 | 去掉「周期为正」 | 被抓（1 红） | 周期 `00:00:00` |
| M5 | 最长未确认时长等于周期也放行 | 被抓（1 红） | 两值相等 |
| M6 | 去掉 `approvalRecord` 必填 | 被抓（3 红） | 缺记录、空白记录、空节 |
| M7 | 覆盖层只在已批准时写（不写 null 清旧值） | 被抓（7 红） | 不写时清掉现场手改 |
| M8 | 覆盖层写入未规整的原值（记录串进周期） | 被抓（5 红） | 覆盖层取值、落盘取值 |
| M9 | 回读不比较批准值 | 被抓（4 红） | 绑定为空／不同／残留 |
| M10 | 回读把绑定为空当作一致 | 被抓（2 红） | 定义已批准、绑定为空 |
| M11 | 回读不读事件里的字段 | 被抓（7 红） | 回读各组合、回读行 |
| M12 | 绑定为空时不出警告 | 被抓（1 红） | `MAP_STATION_CATALOG_NOT_APPROVED` |
| M13 | 安装器不发警告 | 被抓（1 红） | 安装器源码守卫 |
| M14 | 只删事件模板里的占位符 | **无效变异**：编译器 CA2017 报模板与参数个数不符，编译失败 | — |
| M14b | 事件占位符改名（能编译） | 被抓（1 红） | Host 模板源码守卫 |
| M15 | `Program.cs` 记一个新建的空 options 而不是绑定的 | 被抓（1 红） | `Program.cs` 源码守卫 |
| M16 | 出厂定义的最长未确认时长改成 `00:10:00` | 被抓（7 红） | 出厂取值、落盘取值 |
| C1 | Host 把批准值记成总秒数而不是 `hh:mm:ss` | 被抓（7 红） | 事件字段、端到端回读 |
| C2 | 同 M7，经 .NET 真实绑定看 | 被抓（13 红） | 五份定义绑定、覆盖现场手改 |

有效 18 条，全部被抓；无效 1 条（M14），已用 M14b 替代。

几轮作废的运行没有计入上表，原因都在副本环境：副本最初缺仓库根目录的 `Directory.*.props` 和 `tools/`；变异脚本在
scratch 目录启动 `dotnet`，没读到 `global.json`，用了别的 SDK，报 CA1859（`ControlServer.Application`，与变异无关）。修正后的副本
在未变异时 Host 编译 0 错误、上述测试类 44/44。另：副本里自检总数是 798（工作树 794），多出的 4 条出现在副本编译过之后，推断是有检查会
枚举编译产物目录里的脚本，未追查；这些都不影响上表的「被抓」判定。

## 剩余风险与失败方式

见 PR 正文。
