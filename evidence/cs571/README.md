# control-server#571 证据

并行实例接受两车车队表。全部为本机离线运行，不碰任何机器。

| 文件 | 内容 | 提交 |
| --- | --- | --- |
| `01-test-parallel-red-at-5e551292.txt` | 测试先行提交上的自检：`571 passed, 47 failed`。回读那组最说明问题：Host 少绑一辆、多绑一辆 agv01、任务类型不同，改动前安装器全判 `Pass` | `5e5512924` |
| `02-csharp-red-at-5e551292.txt` | 同一提交上 `ParallelInstanceEffectiveConfigurationTests`：`Failed: 5, Passed: 11`。事件不带 `Fleet`（`KeyNotFoundException`），两车示例被旧校验拒收 | `5e5512924` |
| `03-test-parallel-green-at-9283db0f.txt` | 自检 `619 passed, 0 failed`，无 SKIP（借 Host 构建产物的 SQLite 用例也跑了） | `9283db0f6` |
| `04-reverse-check-green-at-9283db0f.txt` | `Invoke-ReverseCheck.ps1`：`26 passed, 0 failed`（含新增 case 13b） | `9283db0f6` |
| `05-csharp-green-at-9283db0f.txt` | 生效配置、准入版本、旅程选项与全部架构测试：`112 passed, 0 failed` | `9283db0f6` |
| `06-mutations.txt` | 25 条变异的汇总，全部被杀 | 实现提交 `10cb0e0df` 上 |
| `07-csharp-mutations-run1-red-lines.txt` | 第一轮 C# 变异日志里的红用例行（说明见下） | 同上 |
| `08-mutation-m01-still-refused.txt` | M01 下自检的红用例：每份含 agv01 的定义仍至少有一条拒收理由 | `10cb0e0df` 加变异 |
| `mutate.ps1` | 变异脚本（最终版） | |

## 变异怎么读

每条变异把实现的一道检查改坏，跑自检或 C# 测试类，断言点名的用例变红，跑完从 `HEAD` 还原。M01–M20 改 `ParallelInstance.psm1`
走自检，M21–M25 改 Host 事件或模块走 C# 测试（其中 M23–M25 证明 CI 里的 C# 层也能独立抓住覆盖层与回读的错误）。

跑了三轮，如实记录：

1. 第一轮：M02–M20 有效。M01、M25 的匹配串在文件里出现两次，没有落地。M21–M24 被汇总判成「存活」，
   但那是判读脚本的错：PowerShell 的 `-match` 不区分大小写，测试失败时日志里的 `Error Message:` 被当成编译错误。
   日志本身显示这 4 条都红了（`07-…`）。
2. 第二轮：修正匹配串后补跑 M01，被杀。
3. 第三轮：判读改为只认编译器的 `: error XXnnnn` 后补跑 M21–M25，全部被杀。

M01（agv01 扫描整个关掉）的旁证：含 agv01 的定义仍被拒收，只是理由变成「不是备用车」，即第二道身份核对独立挡得住。
