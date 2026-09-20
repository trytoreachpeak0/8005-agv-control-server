# 这份自检自己有没有判别力（control-server#204）

调度审查问的：**把被测模块改坏，这 9 条自检有几条会红？如果答案是「都不会红」，它就是摆设**——
而我们刚为把它挂进 CI 批了一次越界。

做法是轻量变异测试：对 `L2HmiPhraseWatch.psm1` 逐个注入一处改动，每次只改一处，跑一遍自检，
数有几条红。**七种改法全部被抓到，没有一种是零红。**

| 把模块改成 | 红 / 9 | 红的是哪几条 |
| --- | --- | --- |
| 把读失败的轮算回干净轮 | **2** | a round in which one element could not be read is not a look；a sighting in a failed round is still a sighting |
| 整轮失败也算干净轮 | **6** | a round in which one element could not be read is not a look；a tree that will not enumerate is a failed round, not an exception；a window that is gone is a failed round, not a silent nothing；a tree that enumerates to nothing is not a look either；a sighting in a failed round is still a sighting；a business probe carrying a failing scan still returns its business value |
| 干净轮计数永远是个大数 | **7** | a round that read every element is a clean look；a round in which one element could not be read is not a look；a tree that will not enumerate is a failed round, not an exception；a window that is gone is a failed round, not a silent nothing；a tree that enumerates to nothing is not a look either；a sighting in a failed round is still a sighting；a sampling window stops at the first sighting |
| 采样走完窗口又写 Not reached | **1** | a sampling window that ends without a sighting reports a reading, not "Not reached" |
| 检出不记进 Seen | **2** | a sighting in a failed round is still a sighting；a sampling window stops at the first sighting |
| 扫描重新往外抛异常 | **2** | a tree that will not enumerate is a failed round, not an exception；a business probe carrying a failing scan still returns its business value |
| 空树又算干净轮（本轮新补的那条红线） | **1** | a tree that enumerates to nothing is not a look either |

两种是审查点名要验的：「把读失败计入干净轮」与「让计数永远返回一个大数」，分别红 2 条和 7 条。

最后一行是本轮补上的那条红线自己的护栏（[[new-limit-needs-its-own-guard]] 的要求）：把
`if ($read -eq 0) { $clean = $false }` 拿掉，「空树也算一次看」那条用例立刻红。**新立的限制必须有
东西在它失效时报警**，否则它只是一句注释。

## 附：这条红线是怎么发现的

审查点名问「`L2HmiPhraseWatch.psm1` 在什么都没扫到时报什么」。实测三种空输入：

| `$ElementSource` 返回 | 补之前 | 补之后 |
| --- | --- | --- |
| `@()`（空数组，被管道展开成 `$null`） | 失败轮 ✓ | 失败轮 |
| **`, @()`（不展开，是个真正的空集合对象）** | **干净轮 31 次，`L2-DA-09` 判据 PASS** ✗ | 失败轮 |
| 空 `ArrayList`（像 UIA 的空 collection） | 失败轮 ✓ | 失败轮 |

中间那一格就是这条判据要防的那种假绿：**「扫了 31 轮、零检出」可以是「树空了 31 轮」**。

补之前它在前后两格里是对的，**但对得偶然**——靠的是 PowerShell 把空集合展开成 `$null`，而展开与否
取决于调用点怎么写（`, @()` 就活下来了），调用点改个写法就会翻面，而且翻面时不会有任何东西变红。
补上那一行之后，保证从「PowerShell 恰好会展开」搬到了「代码明确要求这一轮读到过东西」。
