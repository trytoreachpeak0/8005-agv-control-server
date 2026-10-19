# workspace#61 测试证据

被测提交：`0c69c51fa`（`fix/ws61-gate-recovery-journey`），`pwsh -File scripts/parallel/Test-ParallelInstance.ps1`，先 `dotnet build src/ControlServer.Host -c Debug` 供真 SQLite 用例借 provider。

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| `green.txt` | 本提交原样 | 1275 passed, 0 failed |
| `red-A.txt` | 变异 A：删掉 `Get-ParallelDispatchGateRefusal` 关闸分支里整段「点名旅程单独判」 | 1247 passed, 28 failed，全是本票的关闸用例 |
| `red-B.txt` | 变异 B：只删掉「点名旅程那台车有车辆故障就不放」 | 1271 passed, 4 failed，恰好是 4 条车辆故障用例 |

两次变异都只改 `scripts/parallel/ParallelInstance.psm1`，跑完用 `git checkout -- scripts/parallel/ParallelInstance.psm1` 只还原这一个文件，之后 `git status` 干净。
