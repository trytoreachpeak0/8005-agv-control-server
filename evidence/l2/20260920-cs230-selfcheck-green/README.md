# cs#230 自检脚本绿证据：两处漏报补完之后

`after-fix.txt` 是补完 `scripts/l2/Test-L2OnboardHandleAfterRestart.ps1` 两处漏报之后的一次完整运行：

- 主扫描 `scripts/l2/scenarios/` 下 63 个场景，全部 `ok`，没有一处过期句柄；
- 自测重放 `scripts/l2/counterexamples/onboard-handle-after-restart/` 下两个反例，两个都被报出；
- 退出 0。

复现：

```
pwsh -NoProfile -File ./scripts/l2/Test-L2OnboardHandleAfterRestart.ps1
```

补上的两条规则、它们各自的注入验证，以及补之前那两个反例确实没被看见的证据，
在 `evidence/l2/20260920-cs230-selfcheck-red-misses/`。

一处顺带的确认：补上「`& $Context.StopComponent 'onboard-hmi'` 也算重启」之后，现有场景没有一个变红。
`real-onboard-restart-while-waiting-operator.ps1:59` 与 `real-onboard-compensate-then-reconnect.ps1:85`
是仓库里仅有的两处这种写法，它们从 `StopComponent` 到 `RestartOnboard` 之间都没有用过句柄。
`load-result-requires-recovery.ps1` 与 `slot-group-temporarily-full.ps1` 停的是 `'fake-onboard'`，
那是合成对端不是真车载端，判据按参数精确匹配把它们排除在外。
