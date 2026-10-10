# cs#541：g3-slot-fault-declaration 等超时告警时只认本阶段之后的告警

不起桌面的单元级证据，来自 `scripts/l2/Test-L2ExpectedActionOverdue.ps1`（纯输入，几秒）。journey 场景本身没有在这里实跑：journey runner 从绑定提交取场景脚本，要等 cs#393 重移绑定后的重跑来验证。

- `green-output.txt`：本分支上的输出，全部用例符合预期，退出码 0。新增的 7 条用例覆盖 `Select-L2OverdueAlarmSnapshot`：只有装货阶段的旧快照时返回 `$null`（并且证明旧写法——只比码和仓号——会命中它，这正是缺陷）；之后到达但仍列着旧告警的快照不算；卸货阶段新告警被找到；恰好等于下界算；别的仓、别的码、`raisedAt` 缺失或解析不了都不算；没有快照返回 `$null` 而不是报错。
- `mutation-raisedat-bound-removed.diff` 与 `…-output.txt`：把 `raisedAt` 下界改成恒真后，正好是「只有旧快照」「后到但仍列着旧告警」两条变红（`2 case(s) came out wrong.`）。
