# control-server#201 的 L1 红证据

每个文件是一次 `dotnet test --filter` 的失败摘录（英文原文），文件名带被测提交。

| 文件 | 验收项 | 在什么上跑 | 红在哪 |
| --- | --- | --- | --- |
| `01-same-change-one-row-6e533668.txt` | 第 1 条 | 测试提交 `6e533668`（产品代码同 `905ffd1d`） | 变化记录两行（修订 1000 与 2000，同一暂停、同一新名） |
| `02-convergence-failure-64655c71.txt` | 第 2 条 | 测试提交 `64655c71`（产品代码同 `905ffd1d`） | `ExecuteOnceAsync` 抛出 `DbUpdateException` / `SqliteException: database is locked`（`JourneyRuntimeEngine.cs:193`） |
| `02b-final-test-on-unfixed-engine.txt` | 第 2 条 | 定稿测试（需求号改为夹具认得的 `...0001`）配未修的引擎 | 同上，红因不变 |
| `02c-fault-injected-no-detach.txt` | 第 2 条 | 注入故障：保留捕获、去掉解除跟踪 | 本轮后面的保存把失败那次的暂停写进了库（没有变化记录与审计） |
| `05-fault-injected-null-local-allowed.txt` | 第 5 条 | 注入故障：`IsFromThisMachine` 在 `local is null` 时放行 | 三条新测试红（守的是既有正确代码） |
| `06-source-before-body-e6db3eab.txt` | 第 6 条 | 测试提交 `e6db3eab`（只加两个常量桩） | 畸形请求体 400 而非 403；超大请求体被整份读完（262213 字节）；本机超大请求体原因码 `REASON_TOO_LONG` |

独立审查（PR #225）之后补的：

| 文件 | 审查项 | 在什么上跑 | 红在哪 |
| --- | --- | --- | --- |
| `07-review-body-branches-audit.txt` | 应修 2 | 测试提交 `8ff2310b` | 非 JSON 请求被路由按 `.Accepts` 的内容类型以 415 拒掉，到不了处理器：本机来源没有审计，非本机来源得到 415 而不是 403 |
| `07b-fault-injected-no-refusal-audit-empty-refused.txt` | 应修 2 | 注入故障：拒收分支不写审计、空请求体改判拒收 | 415、400、空请求体、超大四条红 |
| `08-review-saved-hold-left-tracked.txt` | 建议 a | 测试提交 `872991d4` | 暂停已保存、变化记录写入失败回滚后，`TaskTypeStationHoldRow` 仍以 Unchanged 留在 ChangeTracker 上 |
| `09-fault-injected-any-row-not-latest.txt` | 建议 b | 注入故障：`FindSameChangeAsync` 比对任一行而非最近一行 | X→Y→X 的第三行没记 |
