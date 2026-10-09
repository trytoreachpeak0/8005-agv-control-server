# control-server#535 证据：`mesIngest.source` 生产来源模式

本票只改 `scripts/parallel/` 下的部署脚本与自测，不改产品代码、不触及协议，所以证据是两份不碰机器的自测
（`Test-ParallelInstance.ps1`、`Invoke-ReverseCheck.ps1`）改前改后的完整输出，加变异与 19 断言的离线核对。
全部在控制端本机跑，没有碰 factory01、生产 MesIngest 或任何车。

## 改前红、改后绿

| 文件 | commit | 结果 |
| --- | --- | --- |
| `red/01-test-parallel-red-at-cd34bca0.txt` | `cd34bca00`（只加了测试与 `production-mes` 定义） | `510 passed, 34 failed`：原有 510 项全绿，新加 34 项全红 |
| `red/02-reverse-check-red-at-cd34bca0.txt` | 同上 | `22 passed, 3 failed`：新加 case 20、21、22 红 |
| `green/01-test-parallel-green-at-1cb58538.txt` | `1cb58538f`（实现） | `544 passed, 0 failed` |
| `green/02-reverse-check-green-at-1cb58538.txt` | 同上 | `25 passed, 0 failed` |

改前红的 34 项里，有几项红的原因是「函数还不存在」（`Format-ParallelMesIngestAudit` 等），
其余是行为红：`production` 定义被当成 `fake` 拒掉（5088、缺替身段），或布局在 StrictMode 下对缺失的
`fakeMesIngest` 取下标而抛出——后者就是「卸载、关闸在 `production` 定义下走不通」的那一条。

## 变异（`mutations/`）

在 `1cb58538f` 上逐个把 `ParallelInstance.psm1` 的关键判断改坏、跑一遍 `Test-ParallelInstance.ps1`、
再用 `git checkout` 还原（文件已提交，还原安全）。脚本是 `mutations/mutate.ps1`，结果 `mutations/mutations.txt`：

| 变异 | 改法 | 被哪项抓住 |
| --- | --- | --- |
| M1 | 通用任务类型规则关掉 | 5 项红，如「another work type beside STAGING_TO_WIRE」 |
| M2 | `WIRE_TO_GATE` 专门分支关掉 | 3 项红，如「the shipped six work types is refused, naming the MVP」 |
| M3 | `production` 下不再拒 `fakeMesIngest` | 1 项红 |
| M4 | 撤替身前不再校验上一份定义 | 1 项红 |
| M5 | `fake` 模式的原有检查整段跳过 | 6 项红（原有用例） |
| M6 | `production` 的 `baseUrl` 不再核对 | 2 项红 |
| M7 | 足迹里保留无名计划任务 | 2 项红 |
| M8 | `source` 大小写不敏感 | 1 项红 |

8 个变异全部被抓住。

没有自动化测试覆盖的部分，如实列出：安装器里真正动机器的几行（`throw $secretRefusal`、撤替身时
`Unregister-ScheduledTask`／`Stop-Process`／删目录、写密钥后的回读）只有 AST 层面的检查
（调用了哪些函数、密钥拒绝在记录定义之前），没有在真机上跑过。

## 19 的按模式断言（`deploy-19/`）

`19-deploy-control-server-parallel.ps1` 在 8005-workspace 仓（本 PR 之外，审过后直接提交 main）。
`deploy-19/test-19.ps1` 从 19 的 AST 里取出 `Assert-MesIngestSourceReported`，用本 PR 的模块与两份出厂定义，
喂 10 组模拟的安装器输出：`ParseFile errors: 0`，10/10 符合预期（`production` 多出 `FAKE_MES_INGEST=`、
报回 `fake`、没有审计行、任务类型带 `WIRE_TO_GATE` 都拒；`fake` 安装缺 `FAKE_MES_INGEST=` 拒；两种模式的回滚只看审计行）。

## 审查第一轮（M1/M2/S1–S4）之后：`review-1/`

审查发现 M1：.NET 配置跨文件按下标合并数组，叠加层的 `["STAGING_TO_WIRE"]` 只盖住包内 `appsettings.json`
六项里的下标 0，Host 实际放行的仍有 `WIRE_TO_GATE`。上面那些红绿都只看定义与叠加文件，所以在 M1 上是假绿（M2）。

| 文件 | commit | 结果 |
| --- | --- | --- |
| `01-csharp-red-at-fbb60434.txt` | `fbb60434e`（只加测试，绑定原样提成 `AddJourneyRuntimeOptions`） | 2 红：`Expected: ["STAGING_TO_WIRE"]`，`Actual: ["STAGING_TO_WIRE", "DIE_TO_OVEN", "WIRE_TO_GATE", "WIRE_TO_OPTICAL", "STAGING_TO_WIRE", ···]` |
| `02-test-parallel-red-at-fbb60434.txt` | 同上 | `548 passed, 12 failed`：读回 Host 实际值的 8 项、S1/S2/S3 与安装器接线 4 项 |
| `03-csharp-green-at-1e7feb88.txt` | `1e7feb88e` | 新测试类、切片账本与 `JourneyRuntimeOptions` 相关共 24 项全过 |
| `04-test-parallel-green-at-d1324770.txt` | `d1324770e` | `560 passed, 0 failed`，没有 SKIP |
| `05-reverse-check-green-at-d1324770.txt` | 同上 | `25 passed, 0 failed` |
| `06-mutation-last-layer-reversed.txt` | 变异：把「取最后一层」改成「取第一层」 | 新测试类 6 项红 5 项 |
| `07-l2-normal-load-host-log-line.txt` | `235acc3e5` 起的工作树 | L2 normal-load 实跑 PASS，真 Host 启动后打出 `EFFECTIVE_CONFIGURATION` 一行 |
| `compact-json-check.*` | `d1324770e` | 用 Host 构建里的 Serilog `CompactJsonFormatter` 真格式化一条事件，`Find-ParallelEffectiveConfiguration` 读出、比对通过 |

S4（第一次报 544、审查跑出 548）：`Test-ParallelInstance.ps1` 里有 4 项 gate reader 用例只在本机存在
`src/ControlServer.Host/bin` 构建时才跑，否则打印 `SKIP` 且不计数。我第一次跑时没有构建（`green/01-...` 里有那行 SKIP），
审查跑的时候有。`review-1/04-...` 是有构建时跑的，560 = 548 + 12。

`deploy-19/` 已换成审查后的版本：15 组模拟输出，含 production 缺 `EFFECTIVE_CONFIGURATION=` 行、带 M1 那六项都拒。
