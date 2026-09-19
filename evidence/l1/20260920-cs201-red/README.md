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
