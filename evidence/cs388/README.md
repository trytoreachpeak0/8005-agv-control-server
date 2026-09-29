# control-server#388 证据

全部在控制端笔记本上跑，2026-09-29，合成装置（`SyntheticOnboard`），经工作区 `Invoke-HeavyLocal.ps1` 启动。

| 目录 | 内容 | 结论 |
| --- | --- | --- |
| `l2-waiting-points-refuses-start-1`、`-2` | 负向场景 `waiting-points-fewer-than-vehicles-refuses-start`，完整保留（含日志与库快照）；`-2` 是最终代码（报错带缺几个、哪些车、命令），`-1` 是改报错之前 | 两次都 PASS |
| `l2-waiting-points-refuses-start-3` | 负向场景，审查后的代码 `a8041ea9`（WPR-03 断言命令里 `--database` 是本轮 stage 的库文件全路径） | PASS |
| `red/l2-wpr03-database-path-placeholder` | 同一场景，把 `WaitingPointStartupCheck` 传给报错的库路径改成 `null`（报错里只剩 `<controlserver.db>` 占位），`refusal-line.log` 是那一行拒绝原文 | FAIL：只有 L2-WPR-03 红，WPR-01/02/04 照旧 PASS |
| `red/l2-startup-check-removed` | 同一场景，去掉 `Program.cs` 里 `WaitingPointStartupCheck.EnsureAsync` 那一行（备份还原，不用 `git checkout`） | FAIL：服务端 120 秒后仍在运行，`/health/live` 答过 |
| `l2-fleet-<场景>` | 8 个 `Fleet` 场景第一轮，`setup.psd1` 一个都没改，编排器默认每车登记一个等待点 | 5 PASS，3 FAIL |
| `l2-fleet-rerun-<场景>` | 第一轮红的 3 个，本机不跑别的重活时重跑 | 3 PASS，服务端日志 0 次车载端静默 |
| `base-af2b02cb/<场景>` | 同样 3 个场景在基线提交 `af2b02cb`（本票开工时的 `fp/v2-impl`，不含本票任何改动）上跑 | 1 PASS，2 FAIL |

多车场景只留 `SUMMARY.md`、`assertions.json`、`timeline.jsonl`，失败的那几份另附 `went-silent.log`（服务端日志里车载端静默那几行的原文）。

## 读这些证据前要知道的两件事

**本票分支的运行都跑在未提交的工作树上。**`SUMMARY.md` 与 `timeline.jsonl` 里的 `controlServerCommit` 是工作树的 `HEAD`，
而那时本票的改动还没提交，所以本票分支的每一次运行都显示 `af2b02cb`——与 `base-af2b02cb/` 那几次**同一个提交号**。
提交号因此分不出哪次是基线、哪次是本票。

**分辨靠 `waiting-points-imported` 这个判据。**它是本票编排器在服务端启动前正式导入等待点时写的（`L2WaitingPoints.psm1`）：

- 本票分支的每个 `Fleet` 场景的 `timeline.jsonl` 里都有一行 `"criterion":"waiting-points-imported"`（值是登记版本号，附站号与覆盖）；
- `base-af2b02cb/` 下三份的 `timeline.jsonl` 里一行都没有——基线的编排器不认识等待点。

`grep -c waiting-points-imported */timeline.jsonl` 一眼可见。

审查之后的两次不一样：负向场景 `-3` 跑在已提交的 `a8041ea9` 上，工作树干净，提交号就是它跑的代码；红证据
`red/l2-wpr03-database-path-placeholder` 也显示 `a8041ea9`，但那是在它上面临时改了一行（见上表），跑完用备份还原。

## 第一轮那 3 个红与本票无关

三个红（`binding-hold-dashboard-not-cascading`、`command-surface-order-hold`、`reassign-when-vehicle-ineligible`）是同一个样子：
合成车载端在同一刻静默 6～9 秒，服务端按 ADR-cross-0027 关掉会话，之后需求一直因 `ONBOARD_FACTS_NOT_READY` 挂在积压里，场景等派车超时。

- 基线提交上同样的场景以**同样的方式**红（`base-af2b02cb/*/went-silent.log`），所以这不是本票的改动带来的。
- 当时本机空闲内存约 2.5 GiB（共 15.3 GiB）；安静时在本票分支上重跑，三个全绿、0 次静默。
- 本票对这些场景的改动只在服务端启动**之前**（假地图多了站 214 起的等待点站、`--migrate-only` 建库、FieldOps 导入），运行时服务端不读登记。

基线上的 `command-surface-order-hold` 那一次与本机一次单元测试重叠，负载更重；`reassign-when-vehicle-ineligible` 那一次没有重叠，照样红。
