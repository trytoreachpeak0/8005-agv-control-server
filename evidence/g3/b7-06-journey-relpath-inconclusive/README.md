# 这一轮无结论，原因在调用方式，不在产品

**先看这一句：14 个场景全都跑过、全都 PASS。**`run-result.json` 里每条 `exitCode: 0`，
`logs/scenario-*.log` 每条都写着 `-> PASS`。出问题的只有最后汇总那一步。

这么开头是因为下面那些数字会吓人：`status = INCONCLUSIVE_RUNNER_ERROR`、114 条断言里
失败 111 条、`gateResults` 是空数组、每个场景的 `outcome` 与 `identity` 都是 `null`。
**那不是产品出了大事，是汇总时一条结论都没读到**，于是每条「应当报告了 X」的断言都落空。

## 根因

`-EvidenceRoot` 传了相对路径 `./evidence/g3/b7-06-journey-final`。场景是在 stage 里那份
control-server 副本的工作目录下跑的，相对路径于是在那边被重新解析，证据落到了

```
C:\w2g-g3-b706\sources\control-server\evidence\g3\b7-06-journey-final\scenarios\
```

而 runner 回到原目录去读，那里一个场景目录都没有——我指定的那个 `scenarios/` 是空的。

## 这一份留着的理由：它的失败方式会骗人

报出来的话是 `No scenario reported a protocol release identity.`——**听起来像协议那边坏了**，
而真正该看的第一眼是「我指定的那个证据目录里到底有没有东西」，那是一眼就能看出来的。
**一条错误信息说的是它观测到的现象，不是根因**；这里现象与根因之间隔着一层路径解析。

一个汇总步骤说「读不到任何结果」时，先去看它读的那个位置，再去理解它为什么读不到：
「读不到」最常见的根因是读错了地方，而错误信息不知道自己在错的地方看。

重跑用的是绝对路径，证据在 `evidence/g3/b7-06-journey-final/`。
已批一张出口前的小票：四个 runner 在参数处拒绝相对路径，汇总步骤在报「读不到结论」之前
先说明它读的绝对路径与那里的条目数，并回扫同一套 stage 机制下其他吃路径的参数。
