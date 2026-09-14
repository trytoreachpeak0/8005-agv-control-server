# L2 CI：`fp/b2-close@2f7433b8` 上合成场景 26 次运行全部 **`PASS`**，轨 B 出口三连全过（只有运行日志，场景证据目录没能上传）

GitHub Actions run [`34807641700`](https://github.com/trytoreachpeak0/8005-agv-control-server/actions/runs/34807641700)（`l2.yml`，`workflow_dispatch`，runner `win11-01` headless）。
`headSha` 为 `2f7433b8`：服务端产品代码与 G3 共享绑定 `052759bc` 相同（其后只有 G3 绑定与证据提交）。

## 为什么要跑

轨 B 在已发布 `protocol-v1.0.0` 上的现行 CI 证据是 `../20260912-ci-34701119449-*`，绑 `a1243a8`。批次 2 收尾期间服务端产品代码改了多处
（`dfdbe9a3`、`5293c43f`、`6e8dea5a`、`1312a512`、`6c252816`、`11f3d69d`、`e62b136d`、`d2a19c7a`、`e6b92ee3`），需要确认轨 B 的出口在新代码上仍成立。

## 结论

「Run synthetic L2 scenarios」这一步 **`success`**，逐条结果（时间为 UTC）：

| 场景 | 次数 | 结果 |
| --- | --- | --- |
| `normal-load`、`session-established-while-moving`、`load-result-requires-recovery`、`load-command-never-answered`、`route-graph-engine`、`create-gate`、`create-gate-unapproved`、`three-synthetic-peers` | 各 1 | 全部 `PASS` |
| `three-vehicle-exit`（轨 B 出口） | 3 | **3/3 `PASS`** |
| `command-surface-order-hold`（轨 B 出口） | 3 | **3/3 `PASS`** |
| `route-graph-staleness`（轨 B 出口） | 3 | **3/3 `PASS`** |
| `emergency-stop-single-trigger`（票 19 的 L2 前置） | 3 | **3/3 `PASS`** |
| `slot-configuration-activation-replay`（批次 3，`batch-3`） | 3 | 3/3 `PASS` |
| `onboard-alarm-snapshot-dashboard`（批次 3，`batch-3`） | 3 | 3/3 `PASS` |

逐行可在 `run.log` 里按 `-> PASS` 查到，每行带着它在 runner 上的证据目录。

## 🔴 这份证据比 9 月 12 日那份薄

**run 的总结论是 `failure`，失败的是「Upload L2 evidence」**：

```
Failed to CreateArtifact: Artifact storage quota has been hit. Unable to upload any new artifacts. Usage is recalculated every 6-12 hours.
```

GitHub 账号的 artifact 存储配额已满（仓库里大量 130 MB 的 `WireToGate-*` 发布包 artifact）。场景自己写的证据目录（`SUMMARY.md`、`assertions.json`、库快照）
只存在于 runner 的临时目录，下一个任务开始前会被清空，**所以没有像 `65bffc0c` 那样把 CI artifact 原样入库**。本目录只有：

- `run.log`：`gh run view --log` 下载的完整运行日志；
- `run.json`：该 run 的元数据（`headSha`、步骤结论、时间）。

能证明的是「这 26 次运行在 `2f7433b8` 上以退出码 0 结束」（编排器 FAIL 时退出 1，工作流按退出码判定）；逐条断言的实读要等配额恢复后重跑一次才有。

## 同一时刻的单元测试

`test.yml` run `34807639494` 第一次在 NuGet 还原阶段失败（runner 连不上 `api.nuget.org` 取包漏洞数据，`NU1900` 被 `TreatWarningsAsErrors` 升为错误），与代码无关；
结果见批次 2 记录的补记。本地同一产品代码全量 722 passed，十片 `CONTROL_SERVER_G2` 全 `PASS`（`../../g2/20260914-protocol-v1.0.0-052759bc/`）。
