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
