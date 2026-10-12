# cs#653 自检取红证据（审查 S3）

本机 2026-10-13，`scripts/l2/Test-L2ScreenState.ps1`，每个文件一次运行，不是连跑。

| 文件 | 改动 | 退出码 | 结果 |
| --- | --- | --- | --- |
| `baseline.txt` | 无 | 0 | 17/17 |
| `S3-value.txt`（`S3-value.diff`） | 停顿点定义 `onboard-compensation-entry=True` 改成 `=true` | 1 | 判出：timeline 把布尔值写成 `True`，`true` 永远走不到 |
| `S3-title.txt`（`S3-title.diff`） | `real-onboard-compensate-then-reconnect` 的 `Invoke-L2RealConfirmedButton` 第 4 个参数（对话框标题）改成 `补偿清空X`，按键名仍是 `补偿清空` | 1 | 判出：场景不再应答标题为 `补偿清空` 的对话框 |

第二条正是审查指出的旧缺口：旧自检只查 `'补偿清空'` 字面量是否出现，而它同时是按键名，所以标题改了照样绿。现在按语法树取确认调用的标题参数核对。每次改动后都用 `git checkout` 还原，运行前核对过只改了一个文件。
