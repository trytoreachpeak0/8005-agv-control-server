# cs#230 自检脚本红证据：两处漏报，以及补完之后各自的注入验证

`scripts/l2/Test-L2OnboardHandleAfterRestart.ps1` 是一个只解析 AST、不起装置的检查（约 1 秒）：
真装置场景在重启车载端之后，必须重新从 `$Context.Onboard` 取驱动句柄，否则旧句柄指向已经关掉的窗口，
`ButtonAvailable` 永远答 `False`——读起来和「车载端根本没给出这个入口」一模一样，只有跑一遍真装置才看得出来
（cs#222 就是这么烧掉一轮的）。

PR #226 引入它时留了两处已知漏报。本目录是它们**确实存在**的证据，以及补完之后**每条新规则各自都在起作用**的证据。

## 一、补之前：两个反例都没被看见

反例在 `scripts/l2/counterexamples/onboard-handle-after-restart/`，各只触发一处漏报：

| 反例 | 触发哪一处漏报 |
| --- | --- |
| `handle-in-another-variable.ps1` | 句柄存在 `$hmi` 里，不叫 `$onboard` |
| `restart-via-stop-component.ps1` | 用 `& $Context.StopComponent 'onboard-hmi'` 杀掉车载端进程，不是 `Invoke-G3UnknownLoad` 也不是 `$Context.RestartOnboard` |

`counterexamples-before-fix.txt`：补之前的脚本对两个反例都报 `ok`、退出 0。

复现（在提交 `c079c4ec` 上，即本票的测试提交，脚本尚未改动）：

```
pwsh -NoProfile -File ./scripts/l2/Test-L2OnboardHandleAfterRestart.ps1 -ScenarioDirectory ./scripts/l2/counterexamples/onboard-handle-after-restart
```

## 二、补之后：各注入一条规则，对应的反例又看不见了

「两个反例都变红」本身还不够——那有可能是其中一条规则顺带盖住了两个。所以把补上的两条规则**分别**改回原样，
各跑一次，看是不是只有它对应的那个反例失守。脚本副本存在本目录，注入点各一行：

| 副本 | 注入的改动 | 期望 |
| --- | --- | --- |
| `inject-a-only-onboard-name.ps1` | 变量名跟踪改回「只认 `$onboard`」 | 反例一漏掉、反例二仍被报出 |
| `inject-b-no-stopcomponent.ps1` | `StopComponent 'onboard-hmi'` 改回「不算重启」 | 反例二漏掉、反例一仍被报出 |

`counterexamples-each-rule-injected.txt` 是这两次的输出，结果与期望一致：两条规则各自独立在起作用，
没有互相顶替。

注入用的是脚本副本，不改仓库里的文件，所以这两次验证不会连带抹掉工作区里未提交的其它改动。

## 三、补之后的绿证据

在 `evidence/l2/20260920-cs230-selfcheck-green/`：主扫描 63 个场景全部干净，两个反例都被报出，退出 0。

补完之后这个检查会在 `test.yml` 里每轮跑一次，并且默认把反例重放一遍（`-SkipSelfTest` 可关）。
这样将来有人把某条规则改坏时，失守的是这一步，而不是安静地变绿——场景本来就该是干净的，
光看主扫描绿说明不了规则还在。
