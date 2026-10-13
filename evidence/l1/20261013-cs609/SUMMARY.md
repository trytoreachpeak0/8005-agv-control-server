# cs#609 变异验证（2026-10-13，本机时段 10:18–10:33）

对象：`change/cs609-cargo-holding-5min` 的 `79674669`。调度 Coordinator 10 放行本机时段后跑，
清单是作者的 M1–M4 加上审查第二阶段的 P1、P2、P3a、P3b、P6。

做法：`03-mutate.sh` 逐项执行。每一轮只改一个文件（脚本核对 `git diff --name-only` 恰好 1 个、改动行数非 0，
否则判作废），跑完 `git checkout` 还原并核对工作区干净。P6 在临时分支上把 #676 的 head `1da4ae770`
合进来，跑完删掉临时分支，没有推送。结束时回到 `79674669`，重新编译（exit 0）。

结果：**8 项变异全部变红，没有存活；P6 全绿；没有作废轮次。**

| 编号 | 改了什么 | 预期 | 结果 |
| --- | --- | --- | --- |
| M1 | `RuntimeFixture` 去掉显式的 30 分钟 | 依赖原默认值的持货用例变红 | `dotnet test` exit=1，4 红 / 38（`Batch7CargoHoldingTests` 2 条，`Batch7StationYieldTests` 2 条） |
| M2 | `instance-factory01-v2.json` 改回 `00:30:00` | 每份定义的上限护栏与 S1 变红 | 自检 exit=1，187 红，含「cargo holding: instance-factory01-v2.json states … at most 00:05:00」；S1 `dotnet test` exit=1，红在 `EveryShippedDefinitionBindsTheApprovedPairOverThePackageAppSettings(instance-factory01-v2.json)` |
| M3 | 删掉「必须显式写出」那条拒绝 | 「left out」变红 | 自检 exit=1，1 红「cargo holding refused: left out」 |
| M4 | 上限检查永不触发 | 两条超上限用例变红 | 自检 exit=1，2 红（`00:05:01`、`00:30:00`） |
| P1 | 去掉 `-ForStopDirection` 的放行 | 停止方向用例变红 | 自检 exit=1，5 红（10-07 定义关闸、卸载 `-WhatIf`、`-ForStopDirection` 接受，以及两条持货放行用例） |
| P2 | 安装器的断言加上 `-ForStopDirection` | 「安装器不带此开关」变红 | 自检 exit=1，1 红「Install-ParallelInstanceLocal.ps1 asserts once, -ForStopDirection (absent)」 |
| P3a | 上限比较 `-gt` 改为 `-ge` | 5 分钟本身被拒，大片变红 | 自检 exit=1，187 红 |
| P3b | 上限 5 分钟改为 6 分钟 | `00:05:01` 那条变红 | 自检 exit=1，1 红「refused: 00:05:01」 |
| P6 | 本地合入 #676 `1da4ae770` | 0 失败，持货一节覆盖 9 份定义 | 合并无冲突；自检 exit=0，898/0，持货一节 9 份定义；`ParallelInstanceEffectiveConfigurationTests` exit=0，48/0 |

需要说明的两点：

- M2 和 P3a 红得很宽，原因是自检的基准定义 `$baseline` 取自 `instance-factory01-v2.json`。这份定义被拒以后，
  以它为基准的用例也跟着变红。`01-mutation-summary.txt` 只留了每项前 8 条和持货相关的那条，略去的条数写在文末。
- M2 让 S1 变红，很可能是因为 `GenerateOverlay` 先调用定义检查、在那里抛了异常，不一定是 S1 新加的 `InRange`
  断言本身。S1 的断言单独有没有判别力，这一轮没有证明；要证明，得跳过定义检查再跑一次，这一轮没有做。

文件：

- `01-mutation-summary.txt`：脚本逐行日志，包括每轮的退出码、红的用例和还原记录。
- `02-mutation-diffs.txt`：每一轮实际施加的 diff。
- `03-mutate.sh`：驱动脚本。
