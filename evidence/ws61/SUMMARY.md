# workspace#61 测试证据

被测提交：`0c69c51fa`（`fix/ws61-gate-recovery-journey`），`pwsh -File scripts/parallel/Test-ParallelInstance.ps1`，先 `dotnet build src/ControlServer.Host -c Debug` 供真 SQLite 用例借 provider。

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| `green.txt` | 本提交原样 | 1275 passed, 0 failed |
| `red-A.txt` | 变异 A：删掉 `Get-ParallelDispatchGateRefusal` 关闸分支里整段「点名旅程单独判」 | 1247 passed, 28 failed，全是本票的关闸用例 |
| `red-B.txt` | 变异 B：只删掉「点名旅程那台车有车辆故障就不放」 | 1271 passed, 4 failed，恰好是 4 条车辆故障用例 |

两次变异都只改 `scripts/parallel/ParallelInstance.psm1`，跑完用 `git checkout -- scripts/parallel/ParallelInstance.psm1` 只还原这一个文件，之后 `git status` 干净。

## 审查返工之后（`b26e44d6c`）

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| `green-2.txt` | 返工后原样（M1～M4、L1～L3，以及 `-State` 逐字） | 1290 passed, 0 failed |
| `red-C.txt` | 变异 C：`ConvertTo-ParallelGateDirection` 末尾的 `throw` 改回 `return 'Open'` | 1289 passed, 1 failed：「映射本身绕过 ValidateSet 时对 closed 抛错」那条 |
| `red-D.txt` | 变异 D：`Set-ParallelDispatchGateLocal.ps1` 里 `Invoke-ParallelDispatchGateChange` 不传 `-RecoveryJourney`（审查 M2 指出的存活变异之一） | 1289 passed, 1 failed：对应的接线守卫 |

C 只改 `ParallelInstance.psm1`，D 只改 `Set-ParallelDispatchGateLocal.ps1`，各自跑完只还原那一个文件，`git status` 干净。
