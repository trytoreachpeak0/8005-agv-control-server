# cs#384 证据索引

批次8-10：看板人工判故障动作（REQ-0359），并入 hmi#247 审查的两个必修项（判定命令的发件箱收尾、判定待答或已生效时拒绝授权装货取消），以及调度批准的对称守卫（装货取消已授权时拒绝判定）。

| 文件 | 内容 |
| --- | --- |
| `red/01-outbox-not-settled.txt` | 必修一取红：基点 `batch-p3/v3@9497b75b` 的产品代码上（只加测试与一个返回 0 的收尾空壳），四条用例全红，身份检查报 `OUTBOX_PROTOCOL_IDENTITY_MISMATCH` 拒绝启动 |
| `red/02-cancel-guard-missing.txt` | 必修二取红：不加守卫时「待答」「已生效」两行 `Expected: "REJECTED" Actual: "AUTHORIZED"`，「无判定」「被拒」两行作为对照保持绿 |
| `red/mutation-M1-cancel-guard-removed.txt` | 去掉取消守卫：两行红在后果断言上，`HasOpenCancellationAsync` 为 `True`（停靠会被永远等不到结果的取消卡住） |
| `red/mutation-M2-reverse-guard-removed.txt` | 去掉对称守卫（取消已授权时拒绝判定）：用例红 |
| `red/mutation-M3-backfill-takes-pending-too.txt` | 启动收尾连 `PENDING` 判定的命令也收：用例红 |
| `pins/00-baselines.txt` | 两份期待动作超时看板基线在 `batch-p3/v3` 与 `fp/v2-impl` 上逐字相同 |
| `pins/01-additions-only.txt` | 钉子重录判据：每份恰好多一处 `,"declaration":null`，删掉后与集成分支旧基线逐字相同；比对脚本自检会报红 |
| `pins/*.actual.txt` | 本票代码跑出的两份新基线原文 |
| `full/` | 本机全量 `dotnet test` 两次的结尾（`c097d8b2` 与最终 head） |
| `targeted.txt` | 最终代码上的定向测试结尾 |
