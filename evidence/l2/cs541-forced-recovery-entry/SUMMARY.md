# cs#541：g3-forced-mechanical-recovery 的确认步骤顺序与失败标题

不起桌面的证据，来自 `scripts/l2/Test-G3ForcedRecoveryEntry.ps1`（几秒，不起窗口）。journey 场景本身没有在这里实跑：journey runner 从绑定提交取场景脚本，要等 cs#393 重移绑定后的重跑来验证。

**缺陷**：车载端（onboard-hmi#216）的「已隔离并完成机械取出」要等两个交接框（`ForcedHandoffSublot`、`ForcedHandoffReceiverName`）都填好才可用。场景却先 `Wait-G3ButtonOffered` 等这个按钮可用（`5f3adc42` 上 :60），再填框（:70-76），所以注定空等 60 秒后记为未到达。失败标题只认「强制机械恢复失败」「确认失败」，不认车载端在结果没上报时实际弹的「强制机械取出未上报」（onboard `b9e67a53` `MainWindow.xaml.cs:336-338`）。

**改法**：先 `Wait-G3ElementPresent` 等交接框出现（不看 IsEnabled），填两个框，再等按钮可用，然后确认。失败标题统一从 `Get-G3ForcedRecoveryFailureTitle` 取，补上「强制机械取出未上报」。

| 文件 | 内容 | 结果 |
| --- | --- | --- |
| `green-output.txt` | 本分支 | 6 条全部符合预期，退出码 0 |
| `red-old-scenario-5f3adc42-output.txt` | 同一自检读 `5f3adc42` 上的旧场景（`old-scenario-5f3adc42.ps1.txt`，即 `git show 5f3adc42:scripts/l2/scenarios/g3-forced-mechanical-recovery.ps1`；改名为 .txt，免得被扫描 .ps1 的检查当成脚本） | 顺序与标题两条变红，退出码 1 |
| `red-mutation-title-dropped-output.txt` | 从 `Get-G3ForcedRecoveryFailureTitle` 去掉「强制机械取出未上报」 | 标题那条变红，退出码 1 |

第一条用例用一个假的车载端驱动（按钮只在两个框都有字时可用）直接演示旧顺序会空等、新顺序能走通。
